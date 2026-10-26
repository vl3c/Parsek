using System;
using System.Collections.Generic;
using System.Linq;

namespace Parsek
{
    /// <summary>
    /// The core recalculation engine. Sorts all game actions by (UT, type, capture sequence),
    /// resets all registered modules, then walks actions forward from UT=0 dispatching
    /// each action to the appropriate tier of modules.
    ///
    /// Pure computation — no KSP state access, no file I/O.
    /// Modules are registered, not hardcoded — the engine does not know about specific module types.
    ///
    /// Tier ordering (design doc 1.8):
    ///   First-tier (independent): Science, Milestones, Contracts, Kerbals
    ///   Transform: Strategies (between tiers — future, placeholder for now)
    ///   Second-tier (dependent): Funds, Reputation
    ///   Parallel: Facilities
    /// </summary>
    internal static class RecalculationEngine
    {
        private static readonly List<IResourceModule> firstTierModules = new List<IResourceModule>();
        private static readonly List<IResourceModule> secondTierModules = new List<IResourceModule>();
        private static IResourceModule strategyTransform;
        private static IResourceModule facilitiesModule;

        // ================================================================
        // Module registration
        // ================================================================

        internal enum ModuleTier
        {
            FirstTier,
            SecondTier,
            Strategy,
            Facilities
        }

        /// <summary>
        /// Registers a resource module for the recalculation walk at the specified tier.
        /// Modules are dispatched in tier order: first-tier, strategy transform, second-tier, facilities.
        /// </summary>
        internal static void RegisterModule(IResourceModule module, ModuleTier tier)
        {
            if (module == null)
            {
                ParsekLog.Warn("RecalcEngine", "RegisterModule called with null module, skipping");
                return;
            }

            switch (tier)
            {
                case ModuleTier.FirstTier:
                    if (firstTierModules.Contains(module))
                    {
                        ParsekLog.Verbose("RecalcEngine",
                            $"First-tier module already registered: {module.GetType().Name}, skipping");
                        return;
                    }
                    firstTierModules.Add(module);
                    ParsekLog.Verbose("RecalcEngine",
                        $"Registered first-tier module: {module.GetType().Name}, total={firstTierModules.Count}");
                    break;

                case ModuleTier.SecondTier:
                    if (secondTierModules.Contains(module))
                    {
                        ParsekLog.Verbose("RecalcEngine",
                            $"Second-tier module already registered: {module.GetType().Name}, skipping");
                        return;
                    }
                    secondTierModules.Add(module);
                    ParsekLog.Verbose("RecalcEngine",
                        $"Registered second-tier module: {module.GetType().Name}, total={secondTierModules.Count}");
                    break;

                case ModuleTier.Strategy:
                    if (strategyTransform == module)
                    {
                        ParsekLog.Verbose("RecalcEngine",
                            $"Strategy transform module already registered: {module.GetType().Name}, skipping");
                        return;
                    }
                    strategyTransform = module;
                    ParsekLog.Verbose("RecalcEngine",
                        $"Registered strategy transform module: {module.GetType().Name}");
                    break;

                case ModuleTier.Facilities:
                    if (facilitiesModule == module)
                    {
                        ParsekLog.Verbose("RecalcEngine",
                            $"Facilities module already registered: {module.GetType().Name}, skipping");
                        return;
                    }
                    facilitiesModule = module;
                    ParsekLog.Verbose("RecalcEngine",
                        $"Registered facilities module: {module.GetType().Name}");
                    break;

                default:
                    ParsekLog.Warn("RecalcEngine", $"Unknown tier '{tier}' for module {module.GetType().Name}");
                    break;
            }
        }

        /// <summary>
        /// Clears all registered modules. Used for test isolation.
        /// </summary>
        internal static void ClearModules()
        {
            int total = firstTierModules.Count + secondTierModules.Count
                + (strategyTransform != null ? 1 : 0)
                + (facilitiesModule != null ? 1 : 0);

            firstTierModules.Clear();
            secondTierModules.Clear();
            strategyTransform = null;
            facilitiesModule = null;

            ParsekLog.Verbose("RecalcEngine", $"ClearModules: removed={total}");
        }

        // ================================================================
        // Core recalculation
        // ================================================================

        /// <summary>
        /// Runs the full recalculation walk from UT=0 forward:
        ///   1. Sort actions by (UT ascending, earnings before spendings, sequence)
        ///   2. Reset all modules
        ///   3. Walk sorted actions, dispatching each to all tiers
        ///   4. Log summary
        /// </summary>
        /// <param name="actions">All game actions to consider. The list is not mutated.</param>
        /// <param name="utCutoff">
        /// Optional UT cutoff. When non-null, only actions with <c>UT &lt;= utCutoff</c> are
        /// fed to the pre-pass and dispatch walk. Seed actions (FundsInitial / ScienceInitial /
        /// ReputationInitial) are always included regardless of cutoff — they are the baseline
        /// by definition. Used by the rewind path to prevent post-rewind re-credit of events
        /// that occurred after the rewind target. When null, no filtering is applied.
        /// </param>
        /// <param name="deadlineClockUT">
        /// Optional instant contract deadlines are judged against, separate from the walk
        /// cutoff. The Re-Fly invoke walk passes a <c>double.MaxValue</c> cutoff (the whole
        /// committed career) with the live UT here. When null, the cutoff (or the last
        /// action's UT) is the deadline clock, as before.
        /// </param>
        internal static void Recalculate(
            List<GameAction> actions, double? utCutoff = null, double? deadlineClockUT = null)
        {
            if (actions == null)
            {
                ParsekLog.Warn("RecalcEngine", "Recalculate called with null actions list");
                return;
            }

            // UT-cutoff filter (Phase D): drop actions whose UT strictly exceeds the cutoff.
            // Seed actions always survive because they define the session baseline. When no
            // cutoff is supplied we reuse the caller's list directly to avoid an allocation.
            int filteredOut = 0;
            List<GameAction> effective;
            if (utCutoff.HasValue)
            {
                double cutoff = utCutoff.Value;
                effective = new List<GameAction>(actions.Count);
                for (int i = 0; i < actions.Count; i++)
                {
                    var a = actions[i];
                    if (a == null) continue;
                    if (IsSeedType(a.Type) || a.UT <= cutoff)
                        effective.Add(a);
                    else
                        filteredOut++;
                }
            }
            else
            {
                effective = actions;
            }

            // Rule 4's plan (committed-value rule plan section 2.3) needs the debt-free walk
            // of the full timeline. It is built only when some committed cost was captured
            // before a row with an earlier UT (only after a rewind); otherwise no row has a
            // floor and the walk is phase 2's.
            bool needsFloorPlans = ReservedFloorPlan.HasCostCapturedAheadOfALaterLoss(actions);
            List<ReservedFloorPlan> floorPlans = null;
            List<GameAction> projectedTimeline = null;
            if (utCutoff.HasValue || needsFloorPlans)
            {
                // A cutoff walk must leave visible game state at the current UT, but funds
                // and science availability also need full-timeline cashflow to reserve
                // already-committed future spendings. This isolated shadow walk uses cloned
                // modules and suppressed logging to derive future Effective/Transformed/
                // EffectiveScience fields without dispatching future actions through the
                // registered live modules. Projection uses the latest explicit action UT or
                // contract deadline so deadline-only future penalties are synthesized too.
                projectedTimeline = RunProjectionWalk(
                    CopyNonNullActions(actions),
                    ComputeProjectionHorizon(actions)).Sorted;
                if (needsFloorPlans)
                    floorPlans = BuildReservedFloorPlans(projectedTimeline, LedgerRowSet(actions), logPlans: true);
            }

            WalkResult walk = RunWalk(effective, utCutoff, deadlineClockUT, floorPlans);

            if (utCutoff.HasValue)
                ApplyProjectedAvailability(projectedTimeline, utCutoff.Value, LedgerRowSet(actions));

            // 4. Log summary
            string cutoffLabel = utCutoff.HasValue
                ? utCutoff.Value.ToString("R", System.Globalization.CultureInfo.InvariantCulture)
                : "null";
            string stateKey = string.Format(
                System.Globalization.CultureInfo.InvariantCulture,
                "total={0}|after={1}|cutoff={2}|filtered={3}|sorted={4}|first={5}:{6}|strategy={7}|second={8}:{9}|facilities={10}",
                actions.Count,
                effective.Count,
                cutoffLabel,
                filteredOut,
                walk.Sorted.Count,
                firstTierModules.Count,
                walk.FirstTierDispatches,
                walk.StrategyDispatches,
                secondTierModules.Count,
                walk.SecondTierDispatches,
                walk.FacilitiesDispatches);
            string message =
                $"Recalculate complete: actionsTotal={actions.Count}, " +
                $"actionsAfterCutoff={effective.Count}, cutoffUT={cutoffLabel}, " +
                $"filteredOut={filteredOut}, walkedSorted={walk.Sorted.Count}, " +
                $"firstTier={firstTierModules.Count} modules ({walk.FirstTierDispatches} dispatches), " +
                $"strategy={walk.StrategyDispatches} dispatches, " +
                $"secondTier={secondTierModules.Count} modules ({walk.SecondTierDispatches} dispatches), " +
                $"facilities={walk.FacilitiesDispatches} dispatches";
            if (utCutoff.HasValue)
            {
                ParsekLog.Verbose("RecalcEngine", message);
            }
            else
            {
                ParsekLog.VerboseOnChange("RecalcEngine",
                    "recalculate-summary",
                    stateKey,
                    message);
            }
        }

        /// <summary>
        /// A read-only availability probe: the same cutoff walk <see cref="Recalculate"/>
        /// runs (the full-timeline projection, then the walk of the rows up to
        /// <paramref name="utCutoff"/>, then the projected availability), but over CLONES of
        /// the registered modules, so no registered module changes (the kerbals module's
        /// reservations above all: a cutoff walk of the live module drops every hold a
        /// future committed flight owns until the next full recalculation). The action-derived
        /// fields the walk rewrites are restored afterwards, and the probe logs nothing.
        /// Returns the probe's clone of type <typeparamref name="T"/>, or null when none is
        /// registered or it cannot be cloned. The affordability gates and the R&amp;D
        /// Research mark read this.
        /// </summary>
        internal static T ProbeModuleAtCutoff<T>(List<GameAction> actions, double utCutoff)
            where T : class, IResourceModule
        {
            if (actions == null) return null;
            var all = CopyNonNullActions(actions);
            var effective = new List<GameAction>(all.Count);
            for (int i = 0; i < all.Count; i++)
                if (IsSeedType(all[i].Type) || all[i].UT <= utCutoff)
                    effective.Add(all[i]);

            var saved = SnapshotDerivedFields(all);
            try
            {
                using (ParsekLog.SuppressScope())
                {
                    List<GameAction> projected = RunProjectionWalk(
                        CopyNonNullActions(all), ComputeProjectionHorizon(all)).Sorted;
                    HashSet<GameAction> ledgerRows = LedgerRowSet(all);
                    // The same planned walk as Recalculate (plan section 2.3).
                    List<ReservedFloorPlan> floorPlans =
                        ReservedFloorPlan.HasCostCapturedAheadOfALaterLoss(all)
                            ? BuildReservedFloorPlans(projected, ledgerRows, logPlans: false)
                            : null;

                    var first = CreateProjectionModules(firstTierModules);
                    var strategy = CreateProjectionModule(strategyTransform);
                    var second = CreateProjectionModules(secondTierModules);
                    var facilities = CreateProjectionModule(facilitiesModule);
                    RunWalk(effective, utCutoff, first, strategy, second, facilities,
                        plans: floorPlans);

                    ApplyProjectedAvailability(first, projected, utCutoff, ledgerRows);
                    if (strategy != null) ApplyProjectedAvailability(strategy, projected, utCutoff, ledgerRows);
                    ApplyProjectedAvailability(second, projected, utCutoff, ledgerRows);
                    if (facilities != null) ApplyProjectedAvailability(facilities, projected, utCutoff, ledgerRows);

                    return FindModule<T>(first) ?? (strategy as T) ?? FindModule<T>(second) ?? (facilities as T);
                }
            }
            finally
            {
                RestoreDerivedFields(saved);
            }
        }

        /// <summary>
        /// What the committed rows after now ask of one pool, for the click predicates
        /// (committed-value rule plan section 2.5): whether a committed purchase or strategy
        /// cost of the pool lies after now, and the need of those costs over the rows after
        /// now (<see cref="ReservedFloorPlan.NeedOfRowsAfter"/>).
        /// </summary>
        internal struct ClickPoolAhead
        {
            internal StockPool Pool;
            internal bool CostAhead;
            internal int CostRowsAhead;
            internal double Need;
        }

        /// <summary>What <see cref="ProbeClickReservationAtCutoff"/> measured.</summary>
        internal sealed class ClickReservationProbe
        {
            internal readonly List<ClickPoolAhead> Pools = new List<ClickPoolAhead>();
            internal readonly List<IResourceModule> Modules = new List<IResourceModule>();

            internal bool TryGetPool(StockPool pool, out ClickPoolAhead ahead)
            {
                for (int i = 0; i < Pools.Count; i++)
                {
                    if (Pools[i].Pool == pool)
                    {
                        ahead = Pools[i];
                        return true;
                    }
                }
                ahead = default(ClickPoolAhead);
                return false;
            }

            internal T Find<T>() where T : class, IResourceModule
            {
                return FindModule<T>(Modules);
            }
        }

        /// <summary>
        /// The click predicates' read-only probe (committed-value rule plan section 2.5): the
        /// same projection and cutoff walk as <see cref="ProbeModuleAtCutoff{T}"/> over module
        /// clones, plus, per floor pool, what the committed rows after
        /// <paramref name="utCutoff"/> still need (<see cref="ClickPoolAhead"/>), read from the
        /// debt-free projection rows before the cutoff walk resets their derived fields. A
        /// click is captured after every row on the timeline, so "a cost lies ahead" is any
        /// committed cost row after now, whatever its capture order. Logs nothing; derived
        /// fields are restored.
        /// </summary>
        internal static ClickReservationProbe ProbeClickReservationAtCutoff(List<GameAction> actions, double utCutoff)
        {
            var result = new ClickReservationProbe();
            if (actions == null) return result;
            var all = CopyNonNullActions(actions);
            var effective = new List<GameAction>(all.Count);
            for (int i = 0; i < all.Count; i++)
                if (IsSeedType(all[i].Type) || all[i].UT <= utCutoff)
                    effective.Add(all[i]);

            var saved = SnapshotDerivedFields(all);
            try
            {
                using (ParsekLog.SuppressScope())
                {
                    List<GameAction> projected = RunProjectionWalk(
                        CopyNonNullActions(all), ComputeProjectionHorizon(all)).Sorted;
                    HashSet<GameAction> ledgerRows = LedgerRowSet(all);

                    var floorModules = new List<IReservedFloorModule>();
                    CollectFloorModules(firstTierModules, floorModules);
                    if (strategyTransform is IReservedFloorModule s) floorModules.Add(s);
                    CollectFloorModules(secondTierModules, floorModules);
                    if (facilitiesModule is IReservedFloorModule f) floorModules.Add(f);
                    for (int m = 0; m < floorModules.Count; m++)
                        result.Pools.Add(MeasureClickPoolAhead(floorModules[m], projected, ledgerRows, utCutoff));

                    List<ReservedFloorPlan> floorPlans =
                        ReservedFloorPlan.HasCostCapturedAheadOfALaterLoss(all)
                            ? BuildReservedFloorPlans(projected, ledgerRows, logPlans: false)
                            : null;
                    var first = CreateProjectionModules(firstTierModules);
                    var strategy = CreateProjectionModule(strategyTransform);
                    var second = CreateProjectionModules(secondTierModules);
                    var facilities = CreateProjectionModule(facilitiesModule);
                    RunWalk(effective, utCutoff, first, strategy, second, facilities, plans: floorPlans);

                    ApplyProjectedAvailability(first, projected, utCutoff, ledgerRows);
                    if (strategy != null) ApplyProjectedAvailability(strategy, projected, utCutoff, ledgerRows);
                    ApplyProjectedAvailability(second, projected, utCutoff, ledgerRows);
                    if (facilities != null) ApplyProjectedAvailability(facilities, projected, utCutoff, ledgerRows);

                    result.Modules.AddRange(first);
                    if (strategy != null) result.Modules.Add(strategy);
                    result.Modules.AddRange(second);
                    if (facilities != null) result.Modules.Add(facilities);
                }
            }
            finally
            {
                RestoreDerivedFields(saved);
            }
            return result;
        }

        private static ClickPoolAhead MeasureClickPoolAhead(
            IReservedFloorModule module, List<GameAction> projected, HashSet<GameAction> ledgerRows, double utCutoff)
        {
            var rows = new List<ReservedFloorRow>();
            for (int i = 0; i < projected.Count; i++)
            {
                var a = projected[i];
                if (a == null || !(a.UT > utCutoff)) continue;
                double delta;
                bool isCost, floorLimited;
                module.ClassifyFloorRow(a, out delta, out isCost, out floorLimited);
                bool synthetic = !CountsInReserve(a, ledgerRows);
                rows.Add(new ReservedFloorRow
                {
                    Action = a,
                    Delta = synthetic ? 0.0 : delta,
                    IsCost = isCost && !synthetic,
                    FloorLimited = floorLimited,
                    Synthetic = synthetic,
                });
            }
            int costRows;
            double need = ReservedFloorPlan.NeedOfRowsAfter(rows, out costRows);
            return new ClickPoolAhead
            {
                Pool = module.FloorPool,
                CostAhead = costRows > 0,
                CostRowsAhead = costRows,
                Need = need,
            };
        }

        /// <summary>One balance sample of <see cref="WalkBalancesOnClones"/>: the running
        /// funds, science and reputation right after the action at <see cref="UT"/>.</summary>
        internal struct LedgerBalanceSample
        {
            internal double UT;
            internal double Funds;
            internal double Science;
            internal double Reputation;
        }

        /// <summary>What <see cref="WalkBalancesOnClones"/> measured.</summary>
        internal sealed class IsolatedBalanceWalk
        {
            internal readonly List<LedgerBalanceSample> Samples = new List<LedgerBalanceSample>();
            /// <summary>Per input row (by ActionId): the Affordable flag the walk set.</summary>
            internal readonly Dictionary<string, bool> AffordableByActionId =
                new Dictionary<string, bool>(StringComparer.Ordinal);
            /// <summary>Per input row (by ActionId): the Effective flag the walk set.</summary>
            internal readonly Dictionary<string, bool> EffectiveByActionId =
                new Dictionary<string, bool>(StringComparer.Ordinal);
            /// <summary>Per input row (by ActionId) the walk did not count and that may stay
            /// out of a Re-Fly seal, with why (<see cref="ReplayCredit.NotCountedLabel"/>, read
            /// right after this walk). A row absent here seals (rewind design 2.12). An id two
            /// rows share is here only when neither seals.</summary>
            internal readonly Dictionary<string, string> NotCountedByActionId =
                new Dictionary<string, string>(StringComparer.Ordinal);
            /// <summary>True when a science module took part in the walk.</summary>
            internal bool ScienceWalked;
        }

        /// <summary>
        /// A full walk of <paramref name="actions"/> (no cutoff, as the live recalculation
        /// walks) over CLONES of the registered modules, sampling the running funds,
        /// science and reputation after every dispatched action, and reading back each
        /// input row's Affordable / Effective flags and whether the walk counted it
        /// (<see cref="IsolatedBalanceWalk.NotCountedByActionId"/>). Nothing registered changes, no state
        /// is patched, the action-derived fields the walk rewrites are restored, and the
        /// walk logs nothing. The Re-Fly merge dominance check (design section 2.12 Rule 5)
        /// compares two of these, so it passes the same <paramref name="deadlineClockUT"/> to
        /// both: contract deadlines are judged at that instant, not at each list's last action.
        /// </summary>
        internal static IsolatedBalanceWalk WalkBalancesOnClones(
            List<GameAction> actions, double? deadlineClockUT = null)
        {
            var result = new IsolatedBalanceWalk();
            if (actions == null) return result;
            var all = CopyNonNullActions(actions);
            var saved = SnapshotDerivedFields(all);
            try
            {
                using (ParsekLog.SuppressScope())
                {
                    // The same planned walk as Recalculate (plan section 2.3).
                    List<ReservedFloorPlan> floorPlans = null;
                    if (ReservedFloorPlan.HasCostCapturedAheadOfALaterLoss(all))
                    {
                        List<GameAction> projected = RunProjectionWalk(
                            CopyNonNullActions(all), ComputeProjectionHorizon(all)).Sorted;
                        floorPlans = BuildReservedFloorPlans(projected, LedgerRowSet(all), logPlans: false);
                    }

                    var first = CreateProjectionModules(firstTierModules);
                    var strategy = CreateProjectionModule(strategyTransform);
                    var second = CreateProjectionModules(secondTierModules);
                    var facilities = CreateProjectionModule(facilitiesModule);
                    var funds = FindModule<FundsModule>(second) ?? FindModule<FundsModule>(first);
                    var science = FindModule<ScienceModule>(first) ?? FindModule<ScienceModule>(second);
                    var reputation = FindModule<ReputationModule>(second) ?? FindModule<ReputationModule>(first);

                    RunWalk(all, null, first, strategy, second, facilities, deadlineClockUT, plans: floorPlans, afterAction: action =>
                    {
                        result.Samples.Add(new LedgerBalanceSample
                        {
                            UT = action.UT,
                            Funds = funds != null ? funds.GetRunningBalance() : 0.0,
                            Science = science != null ? science.GetRunningScience() : 0.0,
                            Reputation = reputation != null ? reputation.GetRunningRep() : 0.0,
                        });
                    }, beforeRewalk: () => result.Samples.Clear());

                    result.ScienceWalked = science != null;
                    // Older saves can carry two rows with one ActionId: the id is out of the
                    // seal only when NO row with it seals (a counted copy keeps it sealing).
                    var sealingIds = new HashSet<string>(StringComparer.Ordinal);
                    for (int i = 0; i < all.Count; i++)
                    {
                        var a = all[i];
                        if (string.IsNullOrEmpty(a.ActionId)) continue;
                        result.AffordableByActionId[a.ActionId] = a.Affordable;
                        result.EffectiveByActionId[a.ActionId] = a.Effective;
                        string notCounted = ReplayCredit.NotCountedLabel(a, result.ScienceWalked);
                        if (notCounted == null)
                        {
                            sealingIds.Add(a.ActionId);
                            result.NotCountedByActionId.Remove(a.ActionId);
                        }
                        else if (!sealingIds.Contains(a.ActionId))
                        {
                            result.NotCountedByActionId[a.ActionId] = notCounted;
                        }
                    }
                }
            }
            finally
            {
                RestoreDerivedFields(saved);
            }
            return result;
        }

        private static T FindModule<T>(List<IResourceModule> modules) where T : class, IResourceModule
        {
            for (int i = 0; i < modules.Count; i++)
            {
                var m = modules[i] as T;
                if (m != null) return m;
            }
            return null;
        }

        private struct DerivedFields
        {
            internal GameAction Action;
            internal bool Effective;
            internal GameActionNotCountedReason NotCountedReason;
            internal float EffectiveScience;
            internal bool Affordable;
            internal double? UnaffordableRunningScience;
            internal float EffectiveRep;
            internal float TransformedFundsReward;
            internal float TransformedScienceReward;
            internal float TransformedRepReward;
            internal bool RecordPartlyCredited;
            internal float RecordCreditedFunds;
            internal float RecordCreditedRep;
            internal float RecordCreditedScience;
            internal int RecordThresholdsCredited;
            internal int RecordThresholdsTotal;
            internal float TakeBackFunds;
            internal float TakeBackRep;
            internal float TakeBackScience;
            internal string TakeBackWitness;
            internal float ConversionShare;
        }

        /// <summary>The fields <see cref="ResetDerivedFields"/> resets and the module walk
        /// writes on the shared action objects.</summary>
        private static List<DerivedFields> SnapshotDerivedFields(List<GameAction> actions)
        {
            var saved = new List<DerivedFields>(actions.Count);
            for (int i = 0; i < actions.Count; i++)
            {
                var a = actions[i];
                saved.Add(new DerivedFields
                {
                    Action = a,
                    Effective = a.Effective,
                    NotCountedReason = a.NotCountedReason,
                    EffectiveScience = a.EffectiveScience,
                    Affordable = a.Affordable,
                    UnaffordableRunningScience = a.UnaffordableRunningScience,
                    EffectiveRep = a.EffectiveRep,
                    TransformedFundsReward = a.TransformedFundsReward,
                    TransformedScienceReward = a.TransformedScienceReward,
                    TransformedRepReward = a.TransformedRepReward,
                    RecordPartlyCredited = a.RecordPartlyCredited,
                    RecordCreditedFunds = a.RecordCreditedFunds,
                    RecordCreditedRep = a.RecordCreditedRep,
                    RecordCreditedScience = a.RecordCreditedScience,
                    RecordThresholdsCredited = a.RecordThresholdsCredited,
                    RecordThresholdsTotal = a.RecordThresholdsTotal,
                    TakeBackFunds = a.TakeBackFunds,
                    TakeBackRep = a.TakeBackRep,
                    TakeBackScience = a.TakeBackScience,
                    TakeBackWitness = a.TakeBackWitness,
                    ConversionShare = a.ConversionShare
                });
            }
            return saved;
        }

        private static void RestoreDerivedFields(List<DerivedFields> saved)
        {
            for (int i = 0; i < saved.Count; i++)
            {
                var s = saved[i];
                var a = s.Action;
                a.Effective = s.Effective;
                a.NotCountedReason = s.NotCountedReason;
                a.EffectiveScience = s.EffectiveScience;
                a.Affordable = s.Affordable;
                a.UnaffordableRunningScience = s.UnaffordableRunningScience;
                a.EffectiveRep = s.EffectiveRep;
                a.TransformedFundsReward = s.TransformedFundsReward;
                a.TransformedScienceReward = s.TransformedScienceReward;
                a.TransformedRepReward = s.TransformedRepReward;
                a.RecordPartlyCredited = s.RecordPartlyCredited;
                a.RecordCreditedFunds = s.RecordCreditedFunds;
                a.RecordCreditedRep = s.RecordCreditedRep;
                a.RecordCreditedScience = s.RecordCreditedScience;
                a.RecordThresholdsCredited = s.RecordThresholdsCredited;
                a.RecordThresholdsTotal = s.RecordThresholdsTotal;
                a.TakeBackFunds = s.TakeBackFunds;
                a.TakeBackRep = s.TakeBackRep;
                a.TakeBackScience = s.TakeBackScience;
                a.TakeBackWitness = s.TakeBackWitness;
                a.ConversionShare = s.ConversionShare;
            }
        }

        /// <summary>
        /// Returns true if the action type is a session-baseline seed (FundsInitial,
        /// ScienceInitial, ReputationInitial). Seeds are always included in recalculation
        /// regardless of any UT cutoff — they define the starting balance for the walk.
        /// </summary>
        internal static bool IsSeedType(GameActionType type)
        {
            return type == GameActionType.FundsInitial
                || type == GameActionType.ScienceInitial
                || type == GameActionType.ReputationInitial;
        }

        // ================================================================
        // Sort
        // ================================================================

        /// <summary>
        /// Test hook: number of times <see cref="SortActions"/> was invoked. Lets the
        /// conditional-second-sort optimization be asserted (a stable pre-pass performs
        /// one sort per walk; an injecting pre-pass performs two).
        /// </summary>
        internal static int SortActionsCallCountForTesting { get; private set; }

        /// <summary>Test hook: resets <see cref="SortActionsCallCountForTesting"/> to zero.</summary>
        internal static void ResetSortActionsCallCountForTesting()
        {
            SortActionsCallCountForTesting = 0;
        }

        /// <summary>
        /// Sorts actions in walk order (design 3.7, section 3.15 step 2):
        ///   Primary: UT ascending
        ///   Secondary: earning types before spending types
        ///   Tertiary: the same-UT tie of <see cref="CompareSameUtTie(long, int, long, int)"/>
        ///             (capture sequence, then <see cref="GameAction.Sequence"/>)
        ///
        /// Uses LINQ OrderBy for a guaranteed stable sort: rows equal on every key keep
        /// their input order. Returns a new sorted list; the input is not modified.
        /// </summary>
        internal static List<GameAction> SortActions(List<GameAction> actions)
        {
            SortActionsCallCountForTesting++;

            if (actions == null || actions.Count == 0)
                return new List<GameAction>();

            var sorted = actions.OrderBy(a => a, WalkOrderComparer.Instance).ToList();
            LogSameUtTieDecisions(sorted);
            return sorted;
        }

        /// <summary>
        /// The walk order of two rows: UT, then earnings before spendings, then
        /// <see cref="CompareSameUtTie(GameAction, GameAction)"/>. Every reader that must
        /// agree with the walk on which of two rows comes first (a first-wins pick, a
        /// "last row at or before now" read) uses this, never a key of its own.
        /// </summary>
        internal static int CompareWalkOrder(GameAction a, GameAction b)
        {
            if (ReferenceEquals(a, b)) return 0;
            int c = a.UT.CompareTo(b.UT);
            if (c != 0) return c;
            c = (IsEarningType(a.Type) ? 0 : 1).CompareTo(IsEarningType(b.Type) ? 0 : 1);
            if (c != 0) return c;
            return CompareSameUtTie(a, b);
        }

        /// <summary>The same-UT tie of two rows (see the four-argument overload).</summary>
        internal static int CompareSameUtTie(GameAction a, GameAction b)
        {
            return CompareSameUtTie(a.CaptureSequence, a.Sequence, b.CaptureSequence, b.Sequence);
        }

        /// <summary>
        /// The same-UT tie key: the real capture order (<see cref="GameAction.CaptureSequence"/>),
        /// so rows written at one frozen KSC UT keep the order the player made them in.
        /// A row with no capture sequence (0: an older save, or a row not filed yet;
        /// negative: converted from an event an older build captured) sorts BEFORE every
        /// sequenced row at its UT: it was captured before this build stamped anything
        /// (<see cref="Parsek.CaptureSequence.FromEvent"/>), and placing it first keeps an
        /// older save's same-UT order what it was. Two rows with no capture sequence, or
        /// with the same one, fall back to <see cref="GameAction.Sequence"/>, the per-writer
        /// ordinal; rows equal on that too keep their input order (the sorts are stable).
        /// </summary>
        internal static int CompareSameUtTie(long captureSeqA, int sequenceA, long captureSeqB, int sequenceB)
        {
            bool knownA = captureSeqA > 0;
            bool knownB = captureSeqB > 0;
            if (knownA != knownB) return knownA ? 1 : -1;
            if (knownA)
            {
                int c = captureSeqA.CompareTo(captureSeqB);
                if (c != 0) return c;
            }
            return sequenceA.CompareTo(sequenceB);
        }

        private sealed class WalkOrderComparer : IComparer<GameAction>
        {
            internal static readonly WalkOrderComparer Instance = new WalkOrderComparer();
            public int Compare(GameAction x, GameAction y) => CompareWalkOrder(x, y);
        }

        /// <summary>
        /// One summary of the same-UT ties the capture sequence decided in a sort: adjacent
        /// pairs whose capture order reverses their <see cref="GameAction.Sequence"/> order,
        /// and pairs where an unsequenced row was placed before a sequenced one. Logged on
        /// change only, since every recalculation sorts the same ledger several times.
        /// </summary>
        private static void LogSameUtTieDecisions(List<GameAction> sorted)
        {
            int captureOrderDecided = 0;
            int unsequencedFirst = 0;
            for (int i = 1; i < sorted.Count; i++)
            {
                var prev = sorted[i - 1];
                var cur = sorted[i];
                if (prev == null || cur == null || prev.UT != cur.UT) continue;
                if (IsEarningType(prev.Type) != IsEarningType(cur.Type)) continue;
                bool prevKnown = prev.CaptureSequence > 0;
                bool curKnown = cur.CaptureSequence > 0;
                if (prevKnown && curKnown && prev.Sequence > cur.Sequence)
                    captureOrderDecided++;
                else if (!prevKnown && curKnown)
                    unsequencedFirst++;
            }
            if (captureOrderDecided == 0 && unsequencedFirst == 0)
                return;
            var ic = System.Globalization.CultureInfo.InvariantCulture;
            string state = captureOrderDecided.ToString(ic) + "|" + unsequencedFirst.ToString(ic);
            ParsekLog.VerboseOnChange("RecalcEngine", "sort-same-ut-tie", state,
                () => "SortActions: same-UT ties ordered by capture sequence: captureOrderDecided="
                    + captureOrderDecided.ToString(ic)
                    + " (capture order reversed the per-writer Sequence), unsequencedFirst="
                    + unsequencedFirst.ToString(ic)
                    + " (a row with no capture sequence placed before a sequenced one), rows="
                    + sorted.Count.ToString(ic));
        }

        // ================================================================
        // Type classification
        // ================================================================

        /// <summary>
        /// Returns true if the action type represents an earning (credit).
        /// Earnings are processed before spendings at the same UT.
        /// </summary>
        internal static bool IsEarningType(GameActionType type)
        {
            switch (type)
            {
                case GameActionType.ScienceEarning:
                // GameActionType.StrategyScienceCredit IS an earning, and the earlier
                // note arguing it out of this list was wrong on its second half. The
                // first half stands: it is INERT for ordering, because both strategy
                // conversion rows apply their magnitude UNCONDITIONALLY in
                // ScienceModule, so where they sort relative to a same-UT earning
                // cannot change the total. What the note missed is the OTHER consumer.
                // Ledger.Reconcile's earning branch keeps an untagged row
                // UNCONDITIONALLY, while the fall-through "other" branch prunes an
                // untagged row whose UT is past maxUT. Left out of this list, a
                // conversion's science credit is pruned by a maxUT-bounded reconcile
                // while the SAME conversion's untagged FundsEarning yield survives -
                // one movement, one leg kept and one dropped. Listing it is the only
                // classification that keeps the pair together.
                case GameActionType.StrategyScienceCredit:
                // Science lab credit: an earning like ScienceEarning.
                case GameActionType.ScienceLabEarning:
                // The stock pool reference's adjustment: something stock already applied.
                // Listed so Ledger.Reconcile's earning branch keeps the untagged row at any
                // maxUT (a cold load reconciles at 0); its sort position at a shared UT is
                // inert, the walk applies it unclamped.
                case GameActionType.StockPoolAdjustment:
                case GameActionType.FundsEarning:
                case GameActionType.MilestoneAchievement:
                case GameActionType.ContractComplete:
                case GameActionType.ContractParameterComplete:
                case GameActionType.ReputationEarning:
                case GameActionType.KerbalRescue:
                case GameActionType.FundsInitial:
                case GameActionType.ScienceInitial:
                case GameActionType.ReputationInitial:
                    return true;
                default:
                    return false;
            }
        }

        /// <summary>
        /// Returns true if the action type represents a spending (debit).
        /// Spendings are processed after earnings at the same UT.
        /// </summary>
        internal static bool IsSpendingType(GameActionType type)
        {
            switch (type)
            {
                case GameActionType.ScienceSpending:
                // GameActionType.StrategyScienceDebit is DELIBERATELY ABSENT. Two facts,
                // in this order:
                //   (1) it would be INERT for ordering. SortActions keys its secondary
                //       level on IsEarningType ONLY ("earning ? 0 : 1"), so every
                //       non-earning type already sorts after earnings at a shared UT.
                //       The exchange's same-UT funds credit / science debit pair is
                //       correctly ordered without an arm here (pinned by
                //       StrategyCaptureTests' same-UT end-to-end cell).
                //   (2) it would be HARMFUL for pruning. The real consumer of
                //       IsSpendingType is Ledger.Reconcile, whose spending branch prunes
                //       any row with UT > maxUT even when a valid RecordingId owns it.
                //       A BUG-F-family cold-load Reconcile runs with maxUT = 0
                //       (Planetarium reports UT 0 on a cold OnLoad), which would
                //       PERMANENTLY DELETE a flight-tagged exchange debit while KSP keeps
                //       the science removed - re-opening the very leak this type closes.
                //       Falling through to Reconcile's generic branch keeps a
                //       valid-id-tagged row regardless of maxUT.
                case GameActionType.FundsSpending:
                case GameActionType.FacilityUpgrade:
                case GameActionType.FacilityRepair:
                case GameActionType.KerbalHire:
                case GameActionType.StrategyActivate:
                case GameActionType.ContractFail:
                case GameActionType.ContractCancel:
                    return true;
                default:
                    return false;
            }
        }

        // ================================================================
        // Internal helpers
        // ================================================================

        private sealed class WalkResult
        {
            internal List<GameAction> Sorted;
            internal int FirstTierDispatches;
            internal int StrategyDispatches;
            internal int SecondTierDispatches;
            internal int FacilitiesDispatches;
        }

        private struct ProjectionResult
        {
            internal double Available;
            internal double MinBalance;
            internal double FinalBalance;
            internal int FutureActions;
            internal int DeltaActions;
            internal int SyntheticRowsExcluded;
            internal double SyntheticDeltaExcluded;
        }

        /// <summary>
        /// The rows the caller handed the walk, by reference. A row of the projected timeline
        /// that is not in it was injected by a module's PrePass (today only
        /// <c>ContractsModule</c>'s synthetic deadline fail of an open contract).
        /// </summary>
        private static HashSet<GameAction> LedgerRowSet(List<GameAction> actions)
        {
            var set = new HashSet<GameAction>();
            if (actions == null)
                return set;
            for (int i = 0; i < actions.Count; i++)
            {
                if (actions[i] != null)
                    set.Add(actions[i]);
            }
            return set;
        }

        /// <summary>
        /// Pure: whether the reserve counts this future row. An open contract's fail penalty is
        /// not held in the pool (committed-value rule plan section 2.2, owner ruling): the
        /// projection's synthetic deadline fail is told apart from ledger rows by membership in
        /// the input list and left out of the reserve, while the projection walk itself (and
        /// <c>ContractsModule</c>'s injection) is unchanged. A null set counts every row.
        /// </summary>
        internal static bool CountsInReserve(GameAction action, HashSet<GameAction> ledgerRows)
        {
            return ledgerRows == null || ledgerRows.Contains(action);
        }

        private static WalkResult RunWalk(
            List<GameAction> actions, double? walkNowUT, double? deadlineClockUT,
            List<ReservedFloorPlan> plans)
        {
            return RunWalk(
                actions,
                walkNowUT,
                firstTierModules,
                strategyTransform,
                secondTierModules,
                facilitiesModule,
                deadlineClockUT,
                plans: plans);
        }

        /// <summary>
        /// Builds one <see cref="ReservedFloorPlan"/> per <see cref="IReservedFloorModule"/>
        /// from the debt-free projection walk's rows (committed-value rule plan section 2.3),
        /// reading each row's delta now, before the visible walk resets the derived fields.
        /// An injected row (not in <paramref name="ledgerRows"/>) is told apart by membership.
        /// </summary>
        private static List<ReservedFloorPlan> BuildReservedFloorPlans(
            List<GameAction> projectedSorted, HashSet<GameAction> ledgerRows, bool logPlans)
        {
            var plans = new List<ReservedFloorPlan>();
            if (projectedSorted == null)
                return plans;
            var floorModules = new List<IReservedFloorModule>();
            CollectFloorModules(firstTierModules, floorModules);
            if (strategyTransform is IReservedFloorModule s) floorModules.Add(s);
            CollectFloorModules(secondTierModules, floorModules);
            if (facilitiesModule is IReservedFloorModule f) floorModules.Add(f);

            for (int m = 0; m < floorModules.Count; m++)
            {
                var module = floorModules[m];
                var rows = new List<ReservedFloorRow>(projectedSorted.Count);
                for (int i = 0; i < projectedSorted.Count; i++)
                {
                    var a = projectedSorted[i];
                    double delta;
                    bool isCost, floorLimited;
                    module.ClassifyFloorRow(a, out delta, out isCost, out floorLimited);
                    bool synthetic = !CountsInReserve(a, ledgerRows);
                    rows.Add(new ReservedFloorRow
                    {
                        Action = a,
                        Delta = synthetic ? 0.0 : delta,
                        IsCost = isCost && !synthetic,
                        FloorLimited = floorLimited,
                        Synthetic = synthetic,
                    });
                }
                plans.Add(ReservedFloorPlan.Build(module.FloorPool, rows));
            }

            if (logPlans)
            {
                var parts = new List<string>(plans.Count);
                for (int i = 0; i < plans.Count; i++)
                    parts.Add(plans[i].Describe());
                string state = string.Join("; ", parts.ToArray());
                ParsekLog.VerboseOnChange("RecalcEngine", "reserved-floor-plans", state,
                    "Reserved floor plans built (a committed cost was captured before a loss with " +
                    "an earlier UT): " + state);
            }
            return plans;
        }

        private static void CollectFloorModules(List<IResourceModule> modules, List<IReservedFloorModule> into)
        {
            for (int i = 0; i < modules.Count; i++)
                if (modules[i] is IReservedFloorModule f)
                    into.Add(f);
        }

        /// <summary>Installs each floor module's plan (after its reset); null plans clear none.</summary>
        private static void InstallFloorPlans(
            List<ReservedFloorPlan> plans,
            List<IResourceModule> firstTier, IResourceModule strategy,
            List<IResourceModule> secondTier, IResourceModule facilities)
        {
            if (plans == null || plans.Count == 0)
                return;
            InstallFloorPlans(plans, firstTier);
            InstallFloorPlan(plans, strategy);
            InstallFloorPlans(plans, secondTier);
            InstallFloorPlan(plans, facilities);
        }

        private static void InstallFloorPlans(List<ReservedFloorPlan> plans, List<IResourceModule> modules)
        {
            for (int i = 0; i < modules.Count; i++)
                InstallFloorPlan(plans, modules[i]);
        }

        private static void InstallFloorPlan(List<ReservedFloorPlan> plans, IResourceModule module)
        {
            var floorModule = module as IReservedFloorModule;
            if (floorModule == null)
                return;
            for (int i = 0; i < plans.Count; i++)
            {
                if (plans[i].Pool == floorModule.FloorPool)
                {
                    floorModule.SetReservedFloorPlan(plans[i]);
                    return;
                }
            }
        }

        private static WalkResult RunProjectionWalk(List<GameAction> actions, double projectionNowUT)
        {
            var projectionFirstTier = CreateProjectionModules(firstTierModules);
            var projectionStrategy = CreateProjectionModule(strategyTransform);
            var projectionSecondTier = CreateProjectionModules(secondTierModules);
            var projectionFacilities = CreateProjectionModule(facilitiesModule);

            using (ParsekLog.SuppressScope())
            {
                return RunWalk(
                    actions,
                    projectionNowUT,
                    projectionFirstTier,
                    projectionStrategy,
                    projectionSecondTier,
                    projectionFacilities);
            }
        }

        private static WalkResult RunWalk(
            List<GameAction> actions,
            double? walkNowUT,
            List<IResourceModule> firstTier,
            IResourceModule strategy,
            List<IResourceModule> secondTier,
            IResourceModule facilities,
            double? deadlineClockUT = null,
            Action<GameAction> afterAction = null,
            List<ReservedFloorPlan> plans = null,
            Action beforeRewalk = null)
        {
            WalkResult result = RunWalkPass(actions, walkNowUT, firstTier, strategy, secondTier, facilities,
                deadlineClockUT, afterAction, plans, null);

            // Conversion legs follow their cause (design 3.15 step 7). A leg is often walked
            // before its cause, so the decision reads this walk's outcome and a second walk
            // applies it, only when a leg actually moves: a career whose causes all count walks
            // once, exactly as before.
            if (!ConversionLegBinding.AnyCausedLeg(result.Sorted))
                return result;
            List<ConversionLegDecision> decisions = ConversionLegBinding.Decide(
                result.Sorted, new HashSet<GameAction>(actions));
            if (!ConversionLegBinding.NeedsRewalk(decisions))
                return result;
            ParsekLog.Verbose("RecalcEngine",
                "Conversion legs follow their cause: walking again with the rescaled legs");
            beforeRewalk?.Invoke();
            return RunWalkPass(actions, walkNowUT, firstTier, strategy, secondTier, facilities,
                deadlineClockUT, afterAction, plans, decisions);
        }

        private static WalkResult RunWalkPass(
            List<GameAction> actions,
            double? walkNowUT,
            List<IResourceModule> firstTier,
            IResourceModule strategy,
            List<IResourceModule> secondTier,
            IResourceModule facilities,
            double? deadlineClockUT,
            Action<GameAction> afterAction,
            List<ReservedFloorPlan> plans,
            List<ConversionLegDecision> legDecisions)
        {
            // 1. Sort — stable sort preserving insertion order for equal keys
            var sorted = SortActions(actions);

            // The walk's input rows, by reference, before any PrePass injects one. A row
            // outside this set (today only ContractsModule's synthetic deadline fail) moves
            // contract state only: the stock pool modules never see it (committed-value
            // rule plan section 2.2). Stock never applied it, and the reference reads the
            // walk's running value as the pool.
            var inputRows = new HashSet<GameAction>(sorted);

            // 2. Reset all modules and action-derived fields.
            ResetAllModules(firstTier, strategy, secondTier, facilities);
            // Rule 4's plan goes in after the reset (which clears it): no plan, no floor.
            InstallFloorPlans(plans, firstTier, strategy, secondTier, facilities);
            ResetDerivedFields(sorted);
            // The conversion legs' shares go in after the reset and before the pre-pass, whose
            // committed-spending sums read them (design 3.15 step 7).
            ConversionLegBinding.Install(legDecisions);

            // 2b. Pre-pass: let modules compute aggregate data before the walk.
            // Modules may inject synthetic actions (e.g., ContractsModule injects
            // ContractFail for expired deadlines), so we re-sort afterward.
            //
            // Pass the cutoff as the walk's effective "now" so ContractsModule can
            // detect deadlines that expired between the last pre-cutoff action and
            // the cutoff itself. Without this, a filtered action list could have its
            // "last UT" land before a deadline, letting deadline-expired contracts
            // slip through the rewind without the synthetic ContractFail.
            bool prePassMutated = PrePassAllModules(
                sorted, inputRows, walkNowUT, deadlineClockUT, firstTier, strategy, secondTier, facilities);
            if (prePassMutated)
                sorted = SortActions(sorted);
            else
                ParsekLog.Verbose("RecalcEngine", "PrePass stable action list; skipped second sort");

            // 3. Walk sorted actions.
            int firstTierDispatches = 0;
            int strategyDispatches = 0;
            int secondTierDispatches = 0;
            int facilitiesDispatches = 0;
            List<GameAction> withheld = null;

            for (int i = 0; i < sorted.Count; i++)
            {
                var action = sorted[i];
                bool injected = !inputRows.Contains(action);
                if (injected)
                {
                    if (withheld == null) withheld = new List<GameAction>();
                    withheld.Add(action);
                }

                // Dispatch to first-tier modules.
                for (int m = 0; m < firstTier.Count; m++)
                {
                    if (injected && firstTier[m] is IStockPoolModule) continue;
                    firstTier[m].ProcessAction(action);
                    firstTierDispatches++;
                }

                // Strategy transform tier.
                if (strategy != null && !(injected && strategy is IStockPoolModule))
                {
                    strategy.ProcessAction(action);
                    strategyDispatches++;
                }

                // Dispatch to second-tier modules.
                for (int m = 0; m < secondTier.Count; m++)
                {
                    if (injected && secondTier[m] is IStockPoolModule) continue;
                    secondTier[m].ProcessAction(action);
                    secondTierDispatches++;
                }

                // Dispatch to facilities module.
                if (facilities != null)
                {
                    facilities.ProcessAction(action);
                    facilitiesDispatches++;
                }

                afterAction?.Invoke(action);
            }

            if (withheld != null)
                LogWithheldInjectedRows(withheld);

            // 4. Post-walk: let modules finalize derived state.
            PostWalkAllModules(firstTier, strategy, secondTier, facilities);

            return new WalkResult
            {
                Sorted = sorted,
                FirstTierDispatches = firstTierDispatches,
                StrategyDispatches = strategyDispatches,
                SecondTierDispatches = secondTierDispatches,
                FacilitiesDispatches = facilitiesDispatches
            };
        }

        /// <summary>
        /// One line per walk that withheld injected rows from the stock pool modules (bounded:
        /// the first five rows are named). The projection and probe walks run under a
        /// suppressed log, so this reads the visible walk.
        /// </summary>
        private static void LogWithheldInjectedRows(List<GameAction> withheld)
        {
            var ic = System.Globalization.CultureInfo.InvariantCulture;
            var parts = new List<string>();
            for (int i = 0; i < withheld.Count && i < 5; i++)
            {
                var a = withheld[i];
                parts.Add(string.Format(ic,
                    "{0} contractId='{1}' ut={2} fundsPenalty={3} repPenalty={4}",
                    a.Type, a.ContractId ?? "(none)", a.UT.ToString("R", ic),
                    a.FundsPenalty.ToString("R", ic), a.RepPenalty.ToString("R", ic)));
            }
            ParsekLog.Info("RecalcEngine",
                $"Walk withheld {withheld.Count.ToString(ic)} injected row(s) from the stock pool modules " +
                $"(contract state only, no pool moves): [{string.Join("; ", parts.ToArray())}]" +
                (withheld.Count > 5 ? " ..." : ""));
        }

        private static void ResetDerivedFields(List<GameAction> sorted)
        {
            // These fields are written by modules during the walk and read by downstream
            // modules within the same walk (e.g., FundsModule reads Effective set by
            // MilestonesModule, ReputationModule reads TransformedRepReward set by
            // StrategiesModule). This reset ensures idempotency: calling Recalculate
            // twice on the same action list produces identical results, with no stale
            // Effective=false / EffectiveScience values surviving after a new walk
            // begins. Transformed reward fields are seeded from their immutable source
            // fields so the strategy transform can subtract from the correct base.
            for (int i = 0; i < sorted.Count; i++)
            {
                sorted[i].Effective = true;
                sorted[i].NotCountedReason = GameActionNotCountedReason.None;
                sorted[i].EffectiveScience = 0f;
                sorted[i].Affordable = false;
                // Cleared alongside Affordable: the pair is what the unaffordable-re-lock
                // guard reads, and a stale non-null marker from a previous walk would make a
                // legitimate re-lock look like a bookkeeping shortfall.
                sorted[i].UnaffordableRunningScience = null;
                sorted[i].EffectiveRep = 0f;
                sorted[i].TransformedFundsReward = sorted[i].FundsReward;
                sorted[i].TransformedScienceReward = sorted[i].ScienceReward;
                sorted[i].TransformedRepReward = sorted[i].RepReward;
                // A world-record row is credited in full until MilestonesModule decides a
                // part of it was credited earlier (design 3.15 step 4).
                sorted[i].RecordPartlyCredited = false;
                sorted[i].RecordCreditedFunds = 0f;
                sorted[i].RecordCreditedRep = 0f;
                sorted[i].RecordCreditedScience = 0f;
                sorted[i].RecordThresholdsCredited = 0;
                sorted[i].RecordThresholdsTotal = 0;
                // Take-back amounts (design 3.15 step 5) are decided by the first tier.
                sorted[i].TakeBackFunds = 0f;
                sorted[i].TakeBackRep = 0f;
                sorted[i].TakeBackScience = 0f;
                sorted[i].TakeBackWitness = null;
                // A conversion leg walks at its full captured amount until the leg pass
                // rescales it (design 3.15 step 7).
                sorted[i].ConversionShare = 1f;
            }
        }

        private static List<GameAction> CopyNonNullActions(List<GameAction> actions)
        {
            var copy = new List<GameAction>(actions.Count);
            for (int i = 0; i < actions.Count; i++)
            {
                if (actions[i] != null)
                    copy.Add(actions[i]);
            }
            return copy;
        }

        /// <summary>
        /// Test hook over <see cref="ComputeProjectionHorizon"/>. The horizon is the seam
        /// where a ContractAccept's deadline meets the funds projection, so it is worth
        /// pinning directly rather than only through a full recalc.
        /// </summary>
        internal static double ComputeProjectionHorizonForTesting(List<GameAction> actions)
        {
            return ComputeProjectionHorizon(actions);
        }

        private static double ComputeProjectionHorizon(List<GameAction> actions)
        {
            double horizon = 0.0;
            bool hasValue = false;

            for (int i = 0; i < actions.Count; i++)
            {
                var action = actions[i];
                if (action == null)
                    continue;

                if (!hasValue || action.UT > horizon)
                {
                    horizon = action.UT;
                    hasValue = true;
                }

                if (action.Type != GameActionType.ContractAccept || double.IsNaN(action.DeadlineUT))
                    continue;

                // CONTRACT-DEADLINE-CAPTURED-AS-DURATION degraded this horizon in BOTH
                // directions while DeadlineUT held a duration: mid-career the duration
                // sat BELOW the real action UTs, so pending deadlines fell outside the
                // projection; early-career it sat far ABOVE them and blew the horizon out
                // to ~8.2e6 for a career at UT ~8000. Capturing the absolute deadline
                // heals both. The implausibility guard is applied here too so a value
                // that was never a UT cannot extend the horizon at all.
                if (ContractsModule.IsImplausibleContractDeadline(action.DeadlineUT, action.UT))
                    continue;

                if (action.DeadlineUT > horizon)
                    horizon = action.DeadlineUT;
            }

            return horizon;
        }

        private static List<IResourceModule> CreateProjectionModules(List<IResourceModule> modules)
        {
            var clones = new List<IResourceModule>(modules.Count);
            for (int i = 0; i < modules.Count; i++)
            {
                var clone = CreateProjectionModule(modules[i]);
                if (clone != null)
                    clones.Add(clone);
            }
            return clones;
        }

        private static IResourceModule CreateProjectionModule(IResourceModule module)
        {
            if (module == null)
                return null;

            try
            {
                var cloneable = module as IProjectionCloneableModule;
                if (cloneable != null)
                {
                    var clone = cloneable.CreateProjectionClone();
                    if (clone != null)
                        return clone;
                }

                return Activator.CreateInstance(module.GetType(), true) as IResourceModule;
            }
            catch (Exception ex)
            {
                ParsekLog.Warn("RecalcEngine",
                    $"Projection clone failed for module {module.GetType().Name}: {ex.GetType().Name}: {ex.Message}");
                return null;
            }
        }

        private static void ApplyProjectedAvailability(
            List<GameAction> projectedTimeline, double cutoff, HashSet<GameAction> ledgerRows)
        {
            if (projectedTimeline == null)
                return;

            ApplyProjectedAvailability(firstTierModules, projectedTimeline, cutoff, ledgerRows);

            if (strategyTransform != null)
                ApplyProjectedAvailability(strategyTransform, projectedTimeline, cutoff, ledgerRows);

            ApplyProjectedAvailability(secondTierModules, projectedTimeline, cutoff, ledgerRows);

            if (facilitiesModule != null)
                ApplyProjectedAvailability(facilitiesModule, projectedTimeline, cutoff, ledgerRows);
        }

        private static void ApplyProjectedAvailability(
            List<IResourceModule> modules,
            List<GameAction> projectedTimeline,
            double cutoff,
            HashSet<GameAction> ledgerRows)
        {
            for (int i = 0; i < modules.Count; i++)
                ApplyProjectedAvailability(modules[i], projectedTimeline, cutoff, ledgerRows);
        }

        private static void ApplyProjectedAvailability(
            IResourceModule module,
            List<GameAction> projectedTimeline,
            double cutoff,
            HashSet<GameAction> ledgerRows)
        {
            var projectionModule = module as ICashflowProjectionModule;
            if (projectionModule == null)
                return;

            double currentBalance = projectionModule.GetProjectionCurrentBalance();
            var projection = ProjectAvailability(
                projectedTimeline,
                cutoff,
                currentBalance,
                projectionModule,
                ledgerRows);
            string moduleName = module.GetType().Name;
            ParsekLog.VerboseOnChange("RecalcEngine",
                "projection-synthetic-excluded|" + moduleName,
                string.Format(System.Globalization.CultureInfo.InvariantCulture, "{0}|{1}|{2}",
                    projection.SyntheticRowsExcluded,
                    projection.SyntheticDeltaExcluded.ToString("R", System.Globalization.CultureInfo.InvariantCulture),
                    cutoff.ToString("R", System.Globalization.CultureInfo.InvariantCulture)),
                $"Projection reserve for {moduleName}: cutoff=" +
                cutoff.ToString("R", System.Globalization.CultureInfo.InvariantCulture) +
                $" excludedSyntheticRows={projection.SyntheticRowsExcluded}" +
                " excludedSyntheticDelta=" +
                projection.SyntheticDeltaExcluded.ToString("R", System.Globalization.CultureInfo.InvariantCulture) +
                " minBalance=" +
                projection.MinBalance.ToString("R", System.Globalization.CultureInfo.InvariantCulture) +
                " (an open contract's synthetic deadline fail is not held in the pool)");
            projectionModule.SetProjectedAvailable(
                projection.Available,
                currentBalance,
                projection.MinBalance,
                projection.FinalBalance,
                projection.FutureActions,
                projection.DeltaActions);
        }

        private static ProjectionResult ProjectAvailability(
            List<GameAction> projectedTimeline,
            double cutoff,
            double currentBalance,
            ICashflowProjectionModule projectionModule,
            HashSet<GameAction> ledgerRows)
        {
            double projected = currentBalance;
            double minProjected = currentBalance;
            int futureActions = 0;
            int deltaActions = 0;
            int syntheticExcluded = 0;
            double syntheticDelta = 0.0;

            for (int i = 0; i < projectedTimeline.Count; i++)
            {
                var action = projectedTimeline[i];
                if (action == null || IsSeedType(action.Type) || action.UT <= cutoff)
                    continue;

                futureActions++;

                double delta;
                if (!projectionModule.TryGetProjectionDelta(action, out delta))
                    continue;

                if (!CountsInReserve(action, ledgerRows))
                {
                    syntheticExcluded++;
                    syntheticDelta += delta;
                    continue;
                }

                projected += delta;
                deltaActions++;

                if (projected < minProjected)
                    minProjected = projected;
            }

            return new ProjectionResult
            {
                Available = minProjected > 0.0 ? minProjected : 0.0,
                MinBalance = minProjected,
                FinalBalance = projected,
                FutureActions = futureActions,
                DeltaActions = deltaActions,
                SyntheticRowsExcluded = syntheticExcluded,
                SyntheticDeltaExcluded = syntheticDelta
            };
        }

        /// <summary>
        /// Runs the pre-pass over every registered module. Returns <c>true</c> when any
        /// module mutated the action list (so the caller must re-sort); <c>false</c> when
        /// the list is left stable and the redundant second sort can be skipped.
        /// </summary>
        private static bool PrePassAllModules(
            List<GameAction> sorted,
            HashSet<GameAction> inputRows,
            double? walkNowUT,
            double? deadlineClockUT,
            List<IResourceModule> firstTier,
            IResourceModule strategy,
            List<IResourceModule> secondTier,
            IResourceModule facilities)
        {
            bool mutated = false;

            for (int i = 0; i < firstTier.Count; i++)
                mutated |= PrePassModule(firstTier[i], sorted, inputRows, walkNowUT, deadlineClockUT);

            if (strategy != null)
                mutated |= PrePassModule(strategy, sorted, inputRows, walkNowUT, deadlineClockUT);

            for (int i = 0; i < secondTier.Count; i++)
                mutated |= PrePassModule(secondTier[i], sorted, inputRows, walkNowUT, deadlineClockUT);

            if (facilities != null)
                mutated |= PrePassModule(facilities, sorted, inputRows, walkNowUT, deadlineClockUT);

            return mutated;
        }

        /// <summary>
        /// Only the contracts module judges deadlines, so only it receives the separate
        /// deadline clock; every other module keeps the walk cutoff as its "now". A stock
        /// pool module's PrePass sees only the walk's input rows (its aggregates must agree
        /// with the dispatch, which withholds an injected row from it).
        /// </summary>
        private static bool PrePassModule(
            IResourceModule module, List<GameAction> sorted, HashSet<GameAction> inputRows,
            double? walkNowUT, double? deadlineClockUT)
        {
            var contracts = module as ContractsModule;
            if (contracts != null && deadlineClockUT.HasValue)
                return contracts.PrePass(sorted, walkNowUT, deadlineClockUT);
            if (module is IStockPoolModule)
                return module.PrePass(InputRowsOnly(sorted, inputRows), walkNowUT);
            return module.PrePass(sorted, walkNowUT);
        }

        /// <summary>The list itself when nothing was injected, else a copy without the injected rows.</summary>
        private static List<GameAction> InputRowsOnly(List<GameAction> sorted, HashSet<GameAction> inputRows)
        {
            if (inputRows == null)
                return sorted;
            bool anyInjected = false;
            for (int i = 0; i < sorted.Count; i++)
            {
                if (!inputRows.Contains(sorted[i])) { anyInjected = true; break; }
            }
            if (!anyInjected)
                return sorted;
            var view = new List<GameAction>(sorted.Count);
            for (int i = 0; i < sorted.Count; i++)
            {
                if (inputRows.Contains(sorted[i]))
                    view.Add(sorted[i]);
            }
            return view;
        }

        private static void PostWalkAllModules(
            List<IResourceModule> firstTier,
            IResourceModule strategy,
            List<IResourceModule> secondTier,
            IResourceModule facilities)
        {
            for (int i = 0; i < firstTier.Count; i++)
                firstTier[i].PostWalk();

            if (strategy != null)
                strategy.PostWalk();

            for (int i = 0; i < secondTier.Count; i++)
                secondTier[i].PostWalk();

            if (facilities != null)
                facilities.PostWalk();
        }

        private static void ResetAllModules(
            List<IResourceModule> firstTier,
            IResourceModule strategy,
            List<IResourceModule> secondTier,
            IResourceModule facilities)
        {
            for (int i = 0; i < firstTier.Count; i++)
                firstTier[i].Reset();

            if (strategy != null)
                strategy.Reset();

            for (int i = 0; i < secondTier.Count; i++)
                secondTier[i].Reset();

            if (facilities != null)
                facilities.Reset();
        }
    }
}
