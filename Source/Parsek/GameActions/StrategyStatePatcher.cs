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
    /// The ledger side of one strategy patch, copied out of the walk's
    /// <see cref="StrategiesModule"/> so a patch that has to wait for stock's strategy list
    /// applies the state of the walk that requested it, not whatever a later (possibly
    /// deferred) walk left in the shared module.
    /// </summary>
    internal sealed class LedgerStrategySnapshot
    {
        internal readonly Dictionary<string, double> ActivateUT = new Dictionary<string, double>(StringComparer.Ordinal);
        internal readonly Dictionary<string, float> Factor = new Dictionary<string, float>(StringComparer.Ordinal);
        internal readonly HashSet<string> Managed = new HashSet<string>(StringComparer.Ordinal);

        internal static LedgerStrategySnapshot From(StrategiesModule strategies)
        {
            var snapshot = new LedgerStrategySnapshot();
            if (strategies == null) return snapshot;
            var ids = strategies.GetActiveStrategyIds();
            for (int i = 0; i < ids.Count; i++)
            {
                StrategiesModule.StrategyState state;
                if (!strategies.TryGetActiveStrategy(ids[i], out state)) continue;
                snapshot.ActivateUT[ids[i]] = state.ActivateUT;
                snapshot.Factor[ids[i]] = state.Commitment;
            }
            snapshot.Managed.UnionWith(strategies.GetManagedStrategyIds());
            return snapshot;
        }
    }

    /// <summary>What stock's strategy list looks like when the patch runs.</summary>
    internal enum StockStrategyListState
    {
        /// <summary><c>StrategySystem.Instance</c> is null.</summary>
        NoSystem,
        /// <summary>The instance exists but its list is still empty: <c>StrategySystem.OnLoad</c>
        /// fills it from a coroutine one frame later.</summary>
        NotLoaded,
        Loaded
    }

    /// <summary>What the stock-load postfix does with a pending strategy patch.</summary>
    internal enum DeferredStrategyPatchDecision
    {
        NoPending,
        /// <summary>The request was made for another <c>StrategySystem</c> instance (a scene
        /// change replaced it) or another save: dropped.</summary>
        Stale,
        /// <summary>Stock finished loading an empty list (no strategy configs): dropped.</summary>
        StillEmpty,
        Run
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
    /// while a live or pending tree exists (<c>GetKspPatchDeferralReason</c>). This patch
    /// runs only on a recalculation, so it cannot switch a strategy off at a committed
    /// deactivation's UT before stock's per-frame <c>Update</c> replays the expiry after a
    /// rewind; <c>StrategyUpdateExpiryPatch</c> does that
    /// (<see cref="StrategyReservationPredicates.DecideStockUpdate"/>), through
    /// <see cref="SwitchOffWithoutCapture"/> and the committed-rows predicate this patch
    /// also reads (<see cref="StrategyCommittedRows.NextRowIsDeactivation"/>).</para>
    ///
    /// <para><b>A late activation expires at once, harmlessly.</b> A patch that switches a
    /// strategy on after its <c>ActivateUT + LongestDuration</c> (for example a recalculation
    /// long after a committed activation) hands KSPCF a strategy already past its duration;
    /// stock's next <c>Update</c> tick expires it, and that is recorded as a real
    /// StrategyDeactivate row at now. One-shot: the next walk has it inactive.</para>
    ///
    /// <para><b>Stock loads its list a frame late.</b> <c>StrategySystem.OnLoad</c> only
    /// starts <c>OnLoadRoutine</c>, which yields one frame and then clears the list and
    /// fills it in the private <c>LoadStrategies(List&lt;ConfigNode&gt;)</c> (KSP 1.12.5).
    /// The ksp-load recalculation runs synchronously inside <c>ParsekScenario.OnLoad</c>,
    /// before that, and may even run before stock's module is added (then
    /// <c>StrategySystem.Instance</c> is null). A patch that finds no loaded list therefore
    /// applies nothing and leaves a one-shot request with a snapshot of the walk's state;
    /// <c>StrategySystemLoadStrategiesPatch</c>, a postfix on <c>LoadStrategies</c>, runs it
    /// the moment the list exists (<see cref="OnStockStrategiesLoaded"/>). A postfix on the
    /// method that fills the list fires exactly once per stock load, at the right moment,
    /// with no polling and no frame budget, and never in a mode without a
    /// <c>StrategySystem</c>. The request is dropped when a newer recalculation starts,
    /// when it was made for another <c>StrategySystem</c> instance (a scene change) or
    /// another save, and it is only made in a Career game.</para>
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

        private static LedgerStrategySnapshot pendingSnapshot;
        private static object pendingSystem;
        private static string pendingSaveFolder;

        /// <summary>True while a strategy patch waits for stock's strategy list.</summary>
        internal static bool HasPendingPatch => pendingSnapshot != null;

        /// <summary>Pure: the state of stock's strategy list.</summary>
        internal static StockStrategyListState ClassifyStockList(bool systemPresent, int strategyCount)
        {
            if (!systemPresent) return StockStrategyListState.NoSystem;
            return strategyCount > 0 ? StockStrategyListState.Loaded : StockStrategyListState.NotLoaded;
        }

        /// <summary>
        /// Pure: whether a patch that found no loaded list should wait for stock's load.
        /// Only in a Career game (the one mode with a <c>StrategySystem</c>).
        /// </summary>
        internal static bool ShouldDeferUntilStockLoad(StockStrategyListState state, bool isCareer)
        {
            return state != StockStrategyListState.Loaded && isCareer;
        }

        /// <summary>
        /// Pure: what the stock-load postfix does. <paramref name="requestedInstance"/> is
        /// null when the request was made before stock's module existed (any instance of
        /// this save then qualifies).
        /// </summary>
        internal static DeferredStrategyPatchDecision DecideDeferredPatch(
            bool hasPending, object requestedInstance, object loadedInstance,
            string requestedSave, string currentSave, int strategyCount)
        {
            if (!hasPending) return DeferredStrategyPatchDecision.NoPending;
            if (requestedInstance != null && !ReferenceEquals(requestedInstance, loadedInstance))
                return DeferredStrategyPatchDecision.Stale;
            if (!string.Equals(requestedSave ?? "", currentSave ?? "", StringComparison.Ordinal))
                return DeferredStrategyPatchDecision.Stale;
            if (strategyCount <= 0) return DeferredStrategyPatchDecision.StillEmpty;
            return DeferredStrategyPatchDecision.Run;
        }

        /// <summary>Drops a waiting strategy patch. Logged when one was waiting.</summary>
        internal static void CancelPendingPatch(string reason)
        {
            if (pendingSnapshot == null) return;
            pendingSnapshot = null;
            pendingSystem = null;
            pendingSaveFolder = null;
            ParsekLog.Verbose(Tag, "PatchStrategies: pending patch dropped (" + (reason ?? "?") + ")");
        }

        internal static void SetPendingPatchForTesting(LedgerStrategySnapshot snapshot, object system, string saveFolder)
        {
            pendingSnapshot = snapshot;
            pendingSystem = system;
            pendingSaveFolder = saveFolder;
        }

        /// <summary>
        /// Called by the <c>StrategySystem.LoadStrategies</c> postfix once stock's list exists:
        /// runs a waiting patch against it, once.
        /// </summary>
        internal static void OnStockStrategiesLoaded(Strategies.StrategySystem system)
        {
            if (pendingSnapshot == null) return;
            string save = CurrentSaveFolder();
            int count = system != null && system.Strategies != null ? system.Strategies.Count : 0;
            var decision = DecideDeferredPatch(pendingSnapshot != null, pendingSystem, system,
                pendingSaveFolder, save, count);
            if (decision == DeferredStrategyPatchDecision.NoPending) return;

            var snapshot = pendingSnapshot;
            pendingSnapshot = null;
            pendingSystem = null;
            pendingSaveFolder = null;
            if (decision != DeferredStrategyPatchDecision.Run)
            {
                ParsekLog.Verbose(Tag, "PatchStrategies: pending patch dropped at stock load (" + decision
                                       + ", strategies=" + count.ToString(IC) + ")");
                return;
            }

            ParsekLog.Info(Tag, "PatchStrategies: stock strategy list loaded (" + count.ToString(IC)
                                + " strategies), running the deferred patch");
            try
            {
                using (SuppressionGuard.ResourcesAndReplay())
                    ApplyToLoadedSystem(system, snapshot);
            }
            catch (Exception ex)
            {
                ParsekLog.Warn(Tag, "PatchStrategies: deferred patch threw, stock strategy state left as is: " + ex);
            }
        }

        [MethodImpl(MethodImplOptions.NoInlining)]
        private static string CurrentSaveFolder()
        {
            try
            {
                return HighLogic.SaveFolder ?? "";
            }
            catch (Exception)
            {
                return "";
            }
        }

        [MethodImpl(MethodImplOptions.NoInlining)]
        private static bool IsCareerGame()
        {
            try
            {
                return HighLogic.CurrentGame != null && HighLogic.CurrentGame.Mode == Game.Modes.CAREER;
            }
            catch (Exception)
            {
                return false;
            }
        }

        internal static void ResetPendingForTesting()
        {
            pendingSnapshot = null;
            pendingSystem = null;
            pendingSaveFolder = null;
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
            var snapshot = LedgerStrategySnapshot.From(strategies);
            var system = Strategies.StrategySystem.Instance;
            int count = system != null && system.Strategies != null ? system.Strategies.Count : 0;
            var state = ClassifyStockList(system != null, count);
            if (state != StockStrategyListState.Loaded)
            {
                if (ShouldDeferUntilStockLoad(state, IsCareerGame()))
                {
                    pendingSnapshot = snapshot;
                    pendingSystem = system;
                    pendingSaveFolder = CurrentSaveFolder();
                    ParsekLog.Verbose(Tag, "PatchStrategies: stock strategy list " + state
                        + ", deferring until stock loads it (ledgerActive="
                        + snapshot.ActivateUT.Count.ToString(IC) + ")");
                }
                else
                {
                    ParsekLog.VerboseOnChange(Tag, "patch-skip|strategies|system", state.ToString(),
                        "PatchStrategies: stock strategy list " + state + ", not a Career game - skipping");
                }
                return;
            }

            // A patch against a loaded list supersedes any request still waiting.
            CancelPendingPatch("patched-on-loaded-list");
            ApplyToLoadedSystem(system, snapshot);
        }

        private static void ApplyToLoadedSystem(Strategies.StrategySystem system, LedgerStrategySnapshot snapshot)
        {
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

            double now = Planetarium.GetUniversalTime();
            var index = CommittedFutureIndexCache.Current;
            var ordered = new List<StrategyTagInfo>(system.Strategies.Count);
            for (int i = 0; i < system.Strategies.Count; i++)
            {
                var s = system.Strategies[i];
                ordered.Add(new StrategyTagInfo(s?.Config?.Name, s?.Config != null ? s.GroupTags : null));
            }
            // The same committed-rows predicate the Strategy.Update() prefix holds an expiry
            // on, so the patch never switches off what the prefix keeps on.
            var plan = ComputePlan(snapshot.ActivateUT, snapshot.Managed.Contains, stockActive, byId.Keys, now,
                id =>
                {
                    var rows = index.StrategyRows(id);
                    return rows != null && rows.NextRowIsDeactivation(now);
                },
                ordered);

            int deactivated = 0, activated = 0, failed = 0;
            for (int i = 0; i < plan.ToDeactivate.Count; i++)
            {
                if (SwitchOffWithoutCapture(byId[plan.ToDeactivate[i]], "PatchStrategies")) deactivated++;
                else failed++;
            }
            for (int i = 0; i < plan.ToActivate.Count; i++)
            {
                string id = plan.ToActivate[i];
                var s = byId[id];
                double activateUT;
                float factor;
                if (!snapshot.ActivateUT.TryGetValue(id, out activateUT)) activateUT = now;
                if (!snapshot.Factor.TryGetValue(id, out factor)) factor = s.Factor;
                s.Load(BuildActivationNode(id, activateUT, factor));
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

        private static readonly FieldInfo IsActiveField = typeof(Strategies.Strategy).GetField(
            "isActive", BindingFlags.Instance | BindingFlags.NonPublic);

        /// <summary>
        /// Switches a stock strategy off with no refund and no capture: <c>Unregister()</c>
        /// plus the private <c>isActive</c> flag. <c>Deactivate()</c> is never called, so
        /// <c>StrategyDeactivatePatch</c> appends no ledger row. Callers run it inside
        /// <c>SuppressionGuard.ResourcesAndReplay()</c>. Shared by the state patch and the
        /// <c>Strategy.Update()</c> prefix. False (warned once) when the field is missing.
        /// </summary>
        internal static bool SwitchOffWithoutCapture(Strategies.Strategy strategy, string caller)
        {
            if (strategy == null) return false;
            if (IsActiveField == null)
            {
                if (!isActiveFieldWarned)
                {
                    isActiveFieldWarned = true;
                    ParsekLog.Warn(Tag, (caller ?? "?")
                        + ": Strategy.isActive field not found - committed deactivations cannot be applied");
                }
                return false;
            }
            strategy.Unregister();
            IsActiveField.SetValue(strategy, false);
            return true;
        }

        internal static void RefreshAdministration()
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
            ResetPendingForTesting();
        }
    }
}
