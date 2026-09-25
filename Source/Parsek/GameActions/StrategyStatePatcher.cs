using System;
using System.Collections.Generic;
using System.Globalization;
using System.Reflection;
using System.Runtime.CompilerServices;

namespace Parsek
{
    /// <summary>
    /// What the strategy state patch does to stock <c>StrategySystem</c>: the pure diff
    /// between the ledger's active set and stock's.
    /// </summary>
    internal sealed class StrategyStatePatchPlan
    {
        /// <summary>Ledger-active, stock-inactive: switch on without a charge.</summary>
        internal readonly List<string> ToActivate = new List<string>();
        /// <summary>Stock-active, ledger-inactive, and managed by the ledger: switch off
        /// without a refund.</summary>
        internal readonly List<string> ToDeactivate = new List<string>();
        /// <summary>Ledger-active ids stock does not know (a removed mod's strategy).</summary>
        internal readonly List<string> MissingInStock = new List<string>();
        /// <summary>Ledger-active ids whose committed activation is still ahead of now
        /// (a walk that ran without a current-UT cutoff): left for the walk that reaches them.</summary>
        internal readonly List<string> FutureDated = new List<string>();
        /// <summary>Stock-active, ledger-inactive ids whose earliest committed row still
        /// ahead is a deactivation: the committed timeline keeps them active until then, so
        /// the walk that marked them inactive ran past now. Left on.</summary>
        internal readonly List<string> FutureDeactivation = new List<string>();
        /// <summary>Stock-active ids the ledger never activated (activated before the save
        /// had a ledger): left alone.</summary>
        internal readonly List<string> Unmanaged = new List<string>();
        /// <summary>Ids that, in the state this plan leaves stock in, stock's own conflict
        /// rule would refuse to activate beside the others (both came from the ledger through
        /// some path the activation block did not cover). Reported, never resolved: turning
        /// one off would diverge from the ledger and the next patch would turn it back on.</summary>
        internal readonly List<string> Conflicting = new List<string>();
        /// <summary>Ids already in the ledger's state in stock.</summary>
        internal int Matching;

        internal bool HasChanges => ToActivate.Count > 0 || ToDeactivate.Count > 0;
    }

    /// <summary>
    /// Makes stock <c>StrategySystem</c> match the ledger's active strategy set after a
    /// recalculation (docs/dev/research/stock-ui-reservation-overlays-2026-09-25.md,
    /// section 10 step 5). Without it a committed activation is charged by the walk but
    /// the stock strategy never switches on, and a committed deactivation never switches
    /// it off.
    ///
    /// <para><b>No charge, no refund, no capture.</b> Activation goes through
    /// <c>Strategy.Load(ConfigNode)</c>, which sets the activation date and factor, then
    /// <c>isActive = true; Register();</c> and charges nothing (the walk already charged the
    /// setup cost). Deactivation calls <c>Unregister()</c> and clears the private
    /// <c>isActive</c> flag, because <c>Deactivate()</c> is gated on
    /// <c>CanBeDeactivated</c> (the KSPCF minimum-duration gate). Neither path calls
    /// <c>Activate()</c> / <c>Deactivate()</c>, so <c>StrategyLifecyclePatch</c> appends no
    /// ledger row; the patch also runs inside <c>PatchAll</c>'s replay guard.</para>
    ///
    /// <para><b>Stock auto-expiry.</b> With KSPCF's StrategyDuration fix, stock expires a
    /// strategy through <c>Deactivate()</c>, which <c>StrategyLifecyclePatch</c> captures as a
    /// StrategyDeactivate row (KSC-origin, or tagged to the live recording). The next walk
    /// therefore sees it inactive and this patch leaves it off: an expiry cannot be undone
    /// here, so there is no re-activate / expire loop. The one window where the ledger lags
    /// is a flight's own tagged expiry before the tree commits, and the patch is deferred
    /// while a live or pending tree exists (<c>GetKspPatchDeferralReason</c>).</para>
    /// </summary>
    internal static class StrategyStatePatcher
    {
        private const string Tag = "KspStatePatcher";
        private static readonly CultureInfo IC = CultureInfo.InvariantCulture;
        private const int IdSampleCap = 10;
        private static bool isActiveFieldWarned;

        /// <summary>
        /// Pure diff. <paramref name="ledgerActive"/> maps each ledger-active id to its
        /// activation UT; <paramref name="isManaged"/> answers whether the ledger ever
        /// activated an id; <paramref name="stockActive"/> and <paramref name="stockKnown"/>
        /// are stock's active ids and all ids stock lists;
        /// <paramref name="hasFutureDeactivation"/> answers whether an id's earliest committed
        /// row after <paramref name="nowUT"/> is a deactivation.
        ///
        /// <para>Two guards keep a walk that ran past now (a recalculation without a
        /// current-UT cutoff, such as the commit path) from applying a committed row early:
        /// an activation dated after now waits (<see cref="StrategyStatePatchPlan.FutureDated"/>),
        /// and a strategy the committed timeline deactivates only later stays on
        /// (<see cref="StrategyStatePatchPlan.FutureDeactivation"/>).</para>
        /// </summary>
        internal static StrategyStatePatchPlan ComputePlan(
            IDictionary<string, double> ledgerActive,
            Func<string, bool> isManaged,
            ICollection<string> stockActive,
            ICollection<string> stockKnown,
            double nowUT,
            Func<string, bool> hasFutureDeactivation = null,
            IReadOnlyList<StrategyTagInfo> stockStrategies = null)
        {
            var plan = new StrategyStatePatchPlan();
            var ledgerIds = new List<string>();
            if (ledgerActive != null) ledgerIds.AddRange(ledgerActive.Keys);
            ledgerIds.Sort(StringComparer.Ordinal);

            for (int i = 0; i < ledgerIds.Count; i++)
            {
                string id = ledgerIds[i];
                if (string.IsNullOrEmpty(id)) continue;
                if (stockActive != null && stockActive.Contains(id))
                {
                    plan.Matching++;
                    continue;
                }
                if (stockKnown == null || !stockKnown.Contains(id))
                {
                    plan.MissingInStock.Add(id);
                    continue;
                }
                if (CommittedFutureIndex.IsFuture(ledgerActive[id], nowUT))
                {
                    plan.FutureDated.Add(id);
                    continue;
                }
                plan.ToActivate.Add(id);
            }

            if (stockActive != null)
            {
                var stockIds = new List<string>(stockActive);
                stockIds.Sort(StringComparer.Ordinal);
                for (int i = 0; i < stockIds.Count; i++)
                {
                    string id = stockIds[i];
                    if (string.IsNullOrEmpty(id)) continue;
                    if (ledgerActive != null && ledgerActive.ContainsKey(id)) continue;
                    if (isManaged == null || !isManaged(id)) plan.Unmanaged.Add(id);
                    else if (hasFutureDeactivation != null && hasFutureDeactivation(id)) plan.FutureDeactivation.Add(id);
                    else plan.ToDeactivate.Add(id);
                }
            }

            if (stockStrategies != null)
            {
                var after = new HashSet<string>(StringComparer.Ordinal);
                if (stockActive != null) after.UnionWith(stockActive);
                after.ExceptWith(plan.ToDeactivate);
                after.UnionWith(plan.ToActivate);
                var ids = new List<string>(after);
                ids.Sort(StringComparer.Ordinal);
                for (int i = 0; i < ids.Count; i++)
                {
                    string[] tags = null;
                    for (int j = 0; j < stockStrategies.Count; j++)
                        if (stockStrategies[j].Id == ids[i]) { tags = stockStrategies[j].Tags ?? new string[0]; break; }
                    if (tags == null) continue;
                    var others = new HashSet<string>(after, StringComparer.Ordinal);
                    others.Remove(ids[i]);
                    if (StrategyReservationPredicates.StockHasConflictingActiveStrategies(stockStrategies, others, tags))
                        plan.Conflicting.Add(ids[i]);
                }
            }
            return plan;
        }

        private static string lastConflictReportKey = "";

        /// <summary>
        /// Warns once per distinct conflicting set: a repeated patch over the same state
        /// logs nothing, and an emptied set resets the latch. Returns true when it logged.
        /// </summary>
        internal static bool ReportConflicts(StrategyStatePatchPlan plan)
        {
            string key = plan != null && plan.Conflicting.Count > 0
                ? string.Join(",", plan.Conflicting.ToArray())
                : "";
            if (key == lastConflictReportKey) return false;
            lastConflictReportKey = key;
            if (key.Length == 0) return false;
            ParsekLog.Warn(Tag,
                "PatchStrategies: the ledger has strategies active together that stock's conflict rule would not allow: ["
                + key + "]. Left as the ledger has them; nothing is switched off to resolve it.");
            return true;
        }

        /// <summary>
        /// The node <c>Strategy.Load</c> reads to switch a strategy on: its activation date
        /// and commitment factor. No EFFECT nodes, so each effect keeps its config state.
        /// </summary>
        internal static ConfigNode BuildActivationNode(string strategyId, double activateUT, float factor)
        {
            var node = new ConfigNode("STRATEGY");
            node.AddValue("name", strategyId ?? "");
            node.AddValue("date", activateUT.ToString("R", IC));
            node.AddValue("factor", factor.ToString("R", IC));
            return node;
        }

        /// <summary>The plan's summary line, invariant.</summary>
        internal static string DescribePlan(StrategyStatePatchPlan plan)
        {
            return string.Format(IC,
                "activated={0} deactivated={1} matching={2} missingInStock={3} futureDated={4}" +
                " futureDeactivation={5} unmanaged={6} conflicting={7} activatedIds=[{8}] deactivatedIds=[{9}]",
                plan.ToActivate.Count, plan.ToDeactivate.Count, plan.Matching,
                plan.MissingInStock.Count, plan.FutureDated.Count, plan.FutureDeactivation.Count,
                plan.Unmanaged.Count, plan.Conflicting.Count, Sample(plan.ToActivate), Sample(plan.ToDeactivate));
        }

        private static string Sample(List<string> ids)
        {
            if (ids.Count <= IdSampleCap) return string.Join(",", ids.ToArray());
            return string.Join(",", ids.GetRange(0, IdSampleCap).ToArray())
                   + ",+" + (ids.Count - IdSampleCap).ToString(IC);
        }

        /// <summary>
        /// Writes the ledger's active strategy set into stock. Idempotent: a second call
        /// with no ledger change finds nothing to do.
        /// </summary>
        internal static void PatchStrategies(StrategiesModule strategies)
        {
            if (strategies == null)
            {
                ParsekLog.VerboseOnChange(Tag, "patch-skip|strategies|module", "null",
                    "PatchStrategies: null StrategiesModule - skipping");
                return;
            }
            if (KspStatePatcher.SuppressUnityCallsForTesting)
            {
                ParsekLog.Verbose(Tag, "PatchStrategies: Unity calls suppressed for testing - skipping");
                return;
            }
            try
            {
                PatchStrategiesCore(strategies);
            }
            catch (Exception ex)
            {
                ParsekLog.Warn(Tag, "PatchStrategies: threw, stock strategy state left as is: " + ex);
            }
        }

        [MethodImpl(MethodImplOptions.NoInlining)]
        private static void PatchStrategiesCore(StrategiesModule strategies)
        {
            var system = Strategies.StrategySystem.Instance;
            if (system == null || system.Strategies == null)
            {
                ParsekLog.VerboseOnChange(Tag, "patch-skip|strategies|system", "null",
                    "PatchStrategies: StrategySystem.Instance is null - skipping");
                return;
            }

            var byId = new Dictionary<string, Strategies.Strategy>(StringComparer.Ordinal);
            var stockActive = new HashSet<string>(StringComparer.Ordinal);
            for (int i = 0; i < system.Strategies.Count; i++)
            {
                var s = system.Strategies[i];
                string id = s?.Config?.Name;
                if (string.IsNullOrEmpty(id) || byId.ContainsKey(id)) continue;
                byId[id] = s;
                if (s.IsActive) stockActive.Add(id);
            }

            var ledgerActive = new Dictionary<string, double>(StringComparer.Ordinal);
            var ledgerIds = strategies.GetActiveStrategyIds();
            for (int i = 0; i < ledgerIds.Count; i++)
            {
                StrategiesModule.StrategyState state;
                if (strategies.TryGetActiveStrategy(ledgerIds[i], out state))
                    ledgerActive[ledgerIds[i]] = state.ActivateUT;
            }

            double now = Planetarium.GetUniversalTime();
            var index = CommittedFutureIndexCache.Current;
            var ordered = new List<StrategyTagInfo>(system.Strategies.Count);
            for (int i = 0; i < system.Strategies.Count; i++)
            {
                var s = system.Strategies[i];
                ordered.Add(new StrategyTagInfo(s?.Config?.Name, s?.Config != null ? s.GroupTags : null));
            }
            var plan = ComputePlan(ledgerActive, strategies.IsManagedStrategy, stockActive, byId.Keys, now,
                id =>
                {
                    var next = StrategyReservationPredicates.FirstFutureRow(index, id, now);
                    return next != null && next.Kind == CommittedFutureKind.StrategyDeactivate;
                },
                ordered);

            int deactivated = 0, activated = 0, failed = 0;
            FieldInfo isActiveField = typeof(Strategies.Strategy).GetField(
                "isActive", BindingFlags.Instance | BindingFlags.NonPublic);
            for (int i = 0; i < plan.ToDeactivate.Count; i++)
            {
                if (isActiveField == null)
                {
                    if (!isActiveFieldWarned)
                    {
                        isActiveFieldWarned = true;
                        ParsekLog.Warn(Tag,
                            "PatchStrategies: Strategy.isActive field not found - committed deactivations cannot be applied");
                    }
                    failed++;
                    continue;
                }
                var s = byId[plan.ToDeactivate[i]];
                s.Unregister();
                isActiveField.SetValue(s, false);
                deactivated++;
            }
            for (int i = 0; i < plan.ToActivate.Count; i++)
            {
                string id = plan.ToActivate[i];
                StrategiesModule.StrategyState state;
                strategies.TryGetActiveStrategy(id, out state);
                var s = byId[id];
                s.Load(BuildActivationNode(id, state != null ? state.ActivateUT : now,
                    state != null ? state.Commitment : s.Factor));
                if (s.IsActive) activated++;
                else failed++;
            }

            string summary = DescribePlan(plan) + " failed=" + failed.ToString(IC)
                             + " nowUT=" + now.ToString("F0", IC);
            if (plan.HasChanges)
            {
                ParsekLog.Info(Tag, "PatchStrategies: " + summary);
                RefreshAdministration();
            }
            else
            {
                ParsekLog.VerboseOnChange(Tag, "patch-strategies", summary, "PatchStrategies: no change " + summary);
            }
            ReportConflicts(plan);
            if (plan.MissingInStock.Count > 0)
            {
                ParsekLog.WarnRateLimited(Tag, "patch-strategies-missing|" + string.Join(",", plan.MissingInStock.ToArray()),
                    "PatchStrategies: ledger-active strategies unknown to stock, left off: "
                    + string.Join(",", plan.MissingInStock.ToArray()));
            }
            if (activated + deactivated != plan.ToActivate.Count + plan.ToDeactivate.Count)
            {
                ParsekLog.Warn(Tag, "PatchStrategies: applied " + (activated + deactivated).ToString(IC)
                                    + " of " + (plan.ToActivate.Count + plan.ToDeactivate.Count).ToString(IC)
                                    + " changes");
            }
        }

        private static void RefreshAdministration()
        {
            try
            {
                var admin = KSP.UI.Screens.Administration.Instance;
                if (admin != null) admin.RedrawPanels();
            }
            catch (Exception ex)
            {
                ParsekLog.Warn(Tag, "PatchStrategies: Administration.RedrawPanels threw: " + ex.Message);
            }
        }

        internal static void ResetForTesting()
        {
            isActiveFieldWarned = false;
            lastConflictReportKey = "";
        }
    }
}
