using System;
using System.Collections.Generic;
using System.Globalization;
using System.Runtime.CompilerServices;
using Contracts;

namespace Parsek
{
    /// <summary>One pool's reservation at now, as a click reads it (committed-value rule plan 2.5).</summary>
    internal struct ClickPoolReserve
    {
        internal StockPool Pool;
        /// <summary>A committed purchase or strategy cost of this pool lies after now. Without
        /// one nothing about this pool is refused (V's ruling, plan 2.2).</summary>
        internal bool CostAhead;
        /// <summary>Funds and science: the reservation-aware target at now (the projected
        /// minimum, what the pool patch writes before its 0 floor). Reputation: the running
        /// value at now.</summary>
        internal double Target;
        /// <summary>Reputation: what the committed costs after now still need (funds and
        /// science carry theirs in <see cref="Target"/>). 0 for funds and science.</summary>
        internal double Need;
        /// <summary>The live stock pool, NaN when unknown.</summary>
        internal double Live;
        /// <summary>The open-contract fail penalties counted (0 without a cost ahead).</summary>
        internal double OpenRes;
        /// <summary><c>target - openRes</c> (reputation: <c>running - need - openRes</c>), the
        /// target read no higher than the live pool.</summary>
        internal double Spendable;
    }

    /// <summary>One open (Active) contract's fail penalty, as stock would charge it.</summary>
    internal struct OpenContractPenalty
    {
        internal string Key;
        internal double Funds;
        /// <summary>The nominal reputation penalty (before stock's curve).</summary>
        internal double ReputationNominal;
    }

    /// <summary>The click reservation at one UT (committed-value rule plan 2.5).</summary>
    internal sealed class ClickReservationSnapshot
    {
        internal double UT;
        internal ClickPoolReserve Funds;
        internal ClickPoolReserve Science;
        internal ClickPoolReserve Reputation;
        /// <summary>The open contracts whose penalty was counted in some pool.</summary>
        internal int OpenContractsCounted;
        /// <summary>The keys of the open contracts counted (a Cancel releases its own).</summary>
        internal readonly HashSet<string> CountedContractKeys = new HashSet<string>(StringComparer.Ordinal);

        internal bool AnyCostAhead => Funds.CostAhead || Science.CostAhead || Reputation.CostAhead;

        internal ClickPoolReserve Get(StockPool pool)
        {
            switch (pool)
            {
                case StockPool.Science: return Science;
                case StockPool.Reputation: return Reputation;
                default: return Funds;
            }
        }

        /// <summary>No committed cost ahead in any pool: every click predicate allows.</summary>
        internal static ClickReservationSnapshot Inert(double ut)
        {
            return new ClickReservationSnapshot
            {
                UT = ut,
                Funds = new ClickPoolReserve { Pool = StockPool.Funds, Live = double.NaN },
                Science = new ClickPoolReserve { Pool = StockPool.Science, Live = double.NaN },
                Reputation = new ClickPoolReserve { Pool = StockPool.Reputation, Live = double.NaN },
            };
        }

        internal string Describe()
        {
            return "ut=" + UT.ToString("R", CultureInfo.InvariantCulture)
                + " " + ClickReservation.DescribePool(Funds)
                + " " + ClickReservation.DescribePool(Science)
                + " " + ClickReservation.DescribePool(Reputation)
                + " openContracts=" + OpenContractsCounted.ToString(CultureInfo.InvariantCulture);
        }
    }

    /// <summary>A purchase decision: refused, and the numbers the reason names.</summary>
    internal struct PurchaseRefusal
    {
        internal bool Refused;
        internal StockPool Pool;
        internal double Cost;
        /// <summary>What the click takes from the pool: the cost for funds and science; for
        /// reputation the cost through stock's curve at the live reputation.</summary>
        internal double Charged;
        internal double Spendable;
    }

    /// <summary>The Mission Control click a contract decision is about.</summary>
    internal enum ContractClick
    {
        Accept,
        Decline,
        Cancel,
    }

    /// <summary>What a contract's stock money is, read from the live contract.</summary>
    internal struct ContractMoney
    {
        internal double FundsAdvance;
        internal double FundsFailure;
        internal double ReputationFailure;
        internal double DateAccepted;
        internal double DateDeadline;
    }

    /// <summary>What one Mission Control click does to the pools (post-curve reputation).</summary>
    internal struct ContractClickEffect
    {
        internal double FundsDelta;
        internal double FundsAddedReserve;
        internal double ReputationDelta;
        internal double ReputationAddedReserve;
    }

    /// <summary>A Mission Control decision: refused, and the pool short and by how much.</summary>
    internal struct ContractClickRefusal
    {
        internal bool Refused;
        internal StockPool Pool;
        internal double Shortfall;
    }

    /// <summary>
    /// The click predicates of the committed-value rule (plan section 2.5, design 3.13 rule
    /// 3). Pure: every greyed control and every backstop reads one of these over one
    /// <see cref="ClickReservationSnapshot"/> (the pairing rule). With no committed cost
    /// ahead in a pool, nothing about that pool is refused, and no open penalty is counted
    /// in it (V's ruling, plan 2.2), so a career that never rewinds stays stock.
    /// </summary>
    internal static class ClickReservation
    {
        private static readonly CultureInfo IC = CultureInfo.InvariantCulture;
        private const double Epsilon = 1e-6;

        /// <summary>
        /// One pool's reserve. Funds and science: <c>spendable = min(max(target, 0), live) -
        /// openRes</c>; reputation: <c>spendable = min(running, live) - need - openRes</c>.
        /// The open penalties count only with a cost ahead.
        /// </summary>
        internal static ClickPoolReserve Compose(StockPool pool, bool costAhead, double target, double need,
            double live, double openRes)
        {
            double baseValue = pool == StockPool.Reputation ? target : Math.Max(target, 0.0);
            if (double.IsNaN(baseValue)) baseValue = 0.0;
            if (!double.IsNaN(live) && live < baseValue) baseValue = live;
            double counted = costAhead && openRes > 0.0 ? openRes : 0.0;
            double needUsed = pool == StockPool.Reputation && need > 0.0 ? need : 0.0;
            return new ClickPoolReserve
            {
                Pool = pool,
                CostAhead = costAhead,
                Target = target,
                Need = needUsed,
                Live = live,
                OpenRes = counted,
                Spendable = baseValue - needUsed - counted,
            };
        }

        /// <summary>
        /// The open-contract reserve of one pool at reputation <paramref name="currentRep"/>:
        /// funds the plain fail penalties, reputation each nominal penalty through stock's
        /// curve. Counted only for contracts with no committed outcome after now (the caller
        /// filters; a committed outcome is on the timeline already).
        /// </summary>
        internal static double OpenReserve(StockPool pool, IList<OpenContractPenalty> open, double currentRep)
        {
            if (open == null || pool == StockPool.Science) return 0.0;
            double sum = 0.0;
            for (int i = 0; i < open.Count; i++)
            {
                if (pool == StockPool.Funds)
                {
                    if (open[i].Funds > 0.0) sum += open[i].Funds;
                }
                else
                {
                    sum += CurvedLoss(open[i].ReputationNominal, currentRep);
                }
            }
            return sum;
        }

        /// <summary>The post-curve size of a nominal reputation loss at <paramref name="currentRep"/> (positive).</summary>
        internal static double CurvedLoss(double nominal, double currentRep)
        {
            if (!(nominal > 0.0)) return 0.0;
            float rep = double.IsNaN(currentRep) ? 0f : (float)currentRep;
            var r = ReputationModule.ApplyReputationCurve(-(float)nominal, rep);
            return -r.actualDelta;
        }

        /// <summary>
        /// The purchase predicate: refused when a cost of the pool lies ahead, stock itself
        /// would allow (<c>live &gt;= cost</c>, raw, as stock's own checks compare; a plain
        /// shortage stays stock's), and what the click takes from the pool is above what is
        /// spendable. For reputation that is the cost through stock's curve at the live
        /// reputation (<see cref="ChargedBy"/>): stock charges it through
        /// <c>Reputation.AddReputation</c>, and the need it is compared with is post-curve.
        /// </summary>
        internal static PurchaseRefusal EvaluatePurchase(ClickReservationSnapshot snapshot, StockPool pool, double cost)
        {
            var result = new PurchaseRefusal { Pool = pool, Cost = cost, Charged = cost };
            if (snapshot == null || !(cost > 0.0)) return result;
            ClickPoolReserve r = snapshot.Get(pool);
            result.Spendable = r.Spendable;
            if (!r.CostAhead) return result;
            if (!double.IsNaN(r.Live) && r.Live < cost) return result;
            result.Charged = ChargedBy(r, cost);
            result.Refused = result.Charged > r.Spendable + Epsilon;
            return result;
        }

        /// <summary>
        /// What a purchase of <paramref name="cost"/> takes from the pool: the cost itself for
        /// funds and science; for reputation the post-curve loss at the live reputation (the
        /// running value when the live one is unknown).
        /// </summary>
        internal static double ChargedBy(ClickPoolReserve r, double cost)
        {
            if (!(cost > 0.0)) return 0.0;
            if (r.Pool != StockPool.Reputation) return cost;
            // The curve steps once per unit of loss; a cost beyond the whole reputation span
            // (stock clamps the pool to +-1000) is refused either way, so it is read raw.
            if (cost > 2.0 * ReputationModule.RepRange) return cost;
            double at = !double.IsNaN(r.Live) ? r.Live : r.Target;
            if (double.IsNaN(at) || double.IsInfinity(at)) at = 0.0;
            return CurvedLoss(cost, at);
        }

        /// <summary>A purchase over several pools (a strategy's setup): the first pool that refuses.</summary>
        internal static PurchaseRefusal EvaluatePurchase(ClickReservationSnapshot snapshot,
            double fundsCost, double scienceCost, double reputationCost)
        {
            PurchaseRefusal funds = EvaluatePurchase(snapshot, StockPool.Funds, fundsCost);
            if (funds.Refused) return funds;
            PurchaseRefusal science = EvaluatePurchase(snapshot, StockPool.Science, scienceCost);
            if (science.Refused) return science;
            return EvaluatePurchase(snapshot, StockPool.Reputation, reputationCost);
        }

        /// <summary>Unity's <c>Mathf.InverseLerp</c>: clamped to 0..1, 0 when the ends meet.</summary>
        internal static double InverseLerp(double a, double b, double value)
        {
            if (a == b) return 0.0;
            double t = (value - a) / (b - a);
            if (double.IsNaN(t) || t < 0.0) return 0.0;
            return t > 1.0 ? 1.0 : t;
        }

        /// <summary>
        /// What a Mission Control click does to the pools (stock 1.12.5, decompiled
        /// <c>Contracts.Contract</c>): Accept pays the advance and reserves the fail
        /// penalties; Decline charges <c>RepLossDeclined</c>; Cancel charges
        /// <c>Lerp(advance, FundsFailure, t)</c> and <c>Lerp(RepLossDeclined, ReputationFailure, t)</c>
        /// with <c>t = InverseLerp(accepted, deadline, now)</c> and releases the contract's own
        /// reserve when it was counted (<paramref name="releasesOwnReserve"/>). Reputation
        /// amounts are post-curve at <paramref name="currentRep"/>.
        /// </summary>
        internal static ContractClickEffect EffectOf(ContractClick click, ContractMoney money, double now,
            double repLossDeclined, double currentRep, bool releasesOwnReserve)
        {
            var e = new ContractClickEffect();
            switch (click)
            {
                case ContractClick.Accept:
                    e.FundsDelta = money.FundsAdvance > 0.0 ? money.FundsAdvance : 0.0;
                    e.FundsAddedReserve = money.FundsFailure > 0.0 ? money.FundsFailure : 0.0;
                    e.ReputationAddedReserve = CurvedLoss(money.ReputationFailure, currentRep);
                    break;
                case ContractClick.Decline:
                    e.ReputationDelta = -CurvedLoss(repLossDeclined, currentRep);
                    break;
                case ContractClick.Cancel:
                {
                    double t = InverseLerp(money.DateAccepted, money.DateDeadline, now);
                    double funds = money.FundsAdvance + (money.FundsFailure - money.FundsAdvance) * t;
                    double rep = repLossDeclined + (money.ReputationFailure - repLossDeclined) * t;
                    e.FundsDelta = funds > 0.0 ? -funds : 0.0;
                    e.ReputationDelta = -CurvedLoss(rep, currentRep);
                    if (releasesOwnReserve)
                    {
                        e.FundsAddedReserve = -(money.FundsFailure > 0.0 ? money.FundsFailure : 0.0);
                        e.ReputationAddedReserve = -CurvedLoss(money.ReputationFailure, currentRep);
                    }
                    break;
                }
            }
            return e;
        }

        /// <summary>
        /// The Mission Control predicate (ruling L1.3): refused when, in a pool with a cost
        /// ahead, <c>spendable + delta - addedReserve &lt; min(0, spendable)</c>, so a click that
        /// improves an already short pool is never refused. Funds first, then reputation
        /// (stock contracts carry no science).
        /// </summary>
        internal static ContractClickRefusal EvaluateContractClick(ClickReservationSnapshot snapshot, ContractClickEffect effect)
        {
            var result = new ContractClickRefusal();
            if (snapshot == null) return result;
            double shortfall;
            if (PoolRefuses(snapshot.Funds, effect.FundsDelta, effect.FundsAddedReserve, out shortfall))
                return new ContractClickRefusal { Refused = true, Pool = StockPool.Funds, Shortfall = shortfall };
            if (PoolRefuses(snapshot.Reputation, effect.ReputationDelta, effect.ReputationAddedReserve, out shortfall))
                return new ContractClickRefusal { Refused = true, Pool = StockPool.Reputation, Shortfall = shortfall };
            return result;
        }

        internal static bool PoolRefuses(ClickPoolReserve r, double delta, double added, out double shortfall)
        {
            shortfall = 0.0;
            if (!r.CostAhead) return false;
            double after = r.Spendable + delta - added;
            double limit = Math.Min(0.0, r.Spendable);
            if (!(after < limit - Epsilon)) return false;
            shortfall = limit - after;
            return true;
        }

        /// <summary>The unit word of a pool in a reason sentence.</summary>
        internal static string UnitOf(StockPool pool)
        {
            switch (pool)
            {
                case StockPool.Science: return "science";
                case StockPool.Reputation: return "reputation";
                default: return "funds";
            }
        }

        /// <summary>An amount in a pool's own format: funds whole, science and reputation to one decimal.</summary>
        internal static string FormatAmount(StockPool pool, double value)
        {
            if (double.IsNaN(value) || double.IsInfinity(value)) value = 0.0;
            return pool == StockPool.Funds ? value.ToString("N0", IC) : value.ToString("F1", IC);
        }

        internal static string DescribePool(ClickPoolReserve r)
        {
            return r.Pool + "(ahead=" + (r.CostAhead ? "1" : "0")
                + " target=" + r.Target.ToString("R", IC)
                + " need=" + r.Need.ToString("R", IC)
                + " live=" + r.Live.ToString("R", IC)
                + " openRes=" + r.OpenRes.ToString("R", IC)
                + " spendable=" + r.Spendable.ToString("R", IC) + ")";
        }
    }

    /// <summary>
    /// The live side of the click predicates: builds the <see cref="ClickReservationSnapshot"/>
    /// at now from the effective ledger (one cached probe per ledger state and row interval),
    /// the live pools and the live open contracts, and owns the shared reason text. Every
    /// mark and every backstop goes through here, so both read the same snapshot. Inert (no
    /// probe, no live read) while no committed cost row lies after now.
    /// </summary>
    internal static class ClickReservationGate
    {
        private const string Tag = "ClickReserve";
        private static readonly CultureInfo IC = CultureInfo.InvariantCulture;

        /// <summary>Test seam: the live value of a pool (NaN for unknown).</summary>
        internal static Func<StockPool, double> LivePoolProviderForTesting;

        /// <summary>Test seam: the live Active contracts with their fail penalties.</summary>
        internal static Func<IList<OpenContractPenalty>> OpenContractsProviderForTesting;

        /// <summary>Test seam: stock's <c>RepLossDeclined</c>.</summary>
        internal static Func<double> RepLossDeclinedProviderForTesting;

        private static readonly object syncRoot = new object();
        private static object cachedEls;
        private static int cachedLedgerVersion = int.MinValue;
        private static double cachedFromUT = double.NaN;
        private static double cachedUntilUT = double.NaN;
        private static RecalculationEngine.ClickReservationProbe cachedProbe;

        /// <summary>How many probes ran (tests pin the cache and the inert fast path).</summary>
        internal static int ProbeCount { get; private set; }

        /// <summary>The snapshot at the committed-future clock's now.</summary>
        internal static ClickReservationSnapshot Current()
        {
            double now;
            try
            {
                now = CommittedFutureIndexCache.CurrentUT();
            }
            catch (Exception ex)
            {
                ParsekLog.WarnRateLimited(Tag, "click-reserve-clock-failed",
                    "Click reservation clock read failed (" + ex.GetType().Name + ": " + ex.Message
                    + ") - failing open: no click is refused for the reservation");
                return ClickReservationSnapshot.Inert(double.NaN);
            }
            return SnapshotAt(now);
        }

        /// <summary>
        /// The snapshot at <paramref name="now"/>. Runs inside stock click prefixes
        /// (<c>Contract.Accept</c> / <c>Decline</c> / <c>Cancel</c>, <c>OnClickAccept</c>), the
        /// launch test and the funds tooltip, so it fails OPEN: any exception logs one
        /// rate-limited Warn and returns the inert snapshot, which refuses nothing. A Parsek
        /// bug never breaks a stock click.
        /// </summary>
        internal static ClickReservationSnapshot SnapshotAt(double now)
        {
            try
            {
                return SnapshotAtCore(now);
            }
            catch (Exception ex)
            {
                ParsekLog.WarnRateLimited(Tag, "click-reserve-snapshot-failed",
                    "Click reservation snapshot failed at ut=" + now.ToString("R", IC) + " ("
                    + ex.GetType().Name + ": " + ex.Message + ") - failing open: no click is refused for the reservation");
                return ClickReservationSnapshot.Inert(now);
            }
        }

        /// <summary>Test seam: throws inside the snapshot (the fail-open path).</summary>
        internal static Action SnapshotFaultForTesting;

        private static ClickReservationSnapshot SnapshotAtCore(double now)
        {
            var fault = SnapshotFaultForTesting;
            if (fault != null) fault();
            IReadOnlyList<GameAction> els = EffectiveState.ComputeELS();
            if (!HasCostRowAfter(els, now))
                return ClickReservationSnapshot.Inert(now);

            RecalculationEngine.ClickReservationProbe probe = ProbeAt(els, now);
            RecalculationEngine.ClickPoolAhead fundsAhead, scienceAhead, repAhead;
            probe.TryGetPool(StockPool.Funds, out fundsAhead);
            probe.TryGetPool(StockPool.Science, out scienceAhead);
            probe.TryGetPool(StockPool.Reputation, out repAhead);
            if (!fundsAhead.CostAhead && !scienceAhead.CostAhead && !repAhead.CostAhead)
                return ClickReservationSnapshot.Inert(now);

            var funds = probe.Find<FundsModule>();
            var science = probe.Find<ScienceModule>();
            var rep = probe.Find<ReputationModule>();
            double fundsTarget = funds != null ? funds.GetProjectionMinBalance() : double.PositiveInfinity;
            double scienceTarget = science != null ? science.GetProjectionMinBalance() : double.PositiveInfinity;
            double repTarget = rep != null ? rep.GetRunningRep() : double.PositiveInfinity;

            double liveFunds = ReadLive(StockPool.Funds);
            double liveScience = ReadLive(StockPool.Science);
            double liveRep = ReadLive(StockPool.Reputation);
            double repAt = double.IsNaN(liveRep) ? repTarget : Math.Min(repTarget, liveRep);

            var index = CommittedFutureIndexCache.Current;
            var counted = new List<OpenContractPenalty>();
            IList<OpenContractPenalty> open = ReadOpenContracts();
            if (open != null)
            {
                for (int i = 0; i < open.Count; i++)
                {
                    if (StockUiReservationPredicates.CommittedContractResolutionAfter(index, open[i].Key, now) != null)
                        continue;
                    counted.Add(open[i]);
                }
            }

            var snapshot = new ClickReservationSnapshot
            {
                UT = now,
                Funds = ClickReservation.Compose(StockPool.Funds, fundsAhead.CostAhead, fundsTarget, fundsAhead.Need,
                    liveFunds, ClickReservation.OpenReserve(StockPool.Funds, counted, repAt)),
                Science = ClickReservation.Compose(StockPool.Science, scienceAhead.CostAhead, scienceTarget,
                    scienceAhead.Need, liveScience, 0.0),
                Reputation = ClickReservation.Compose(StockPool.Reputation, repAhead.CostAhead, repTarget, repAhead.Need,
                    liveRep, ClickReservation.OpenReserve(StockPool.Reputation, counted, repAt)),
            };
            if (fundsAhead.CostAhead || repAhead.CostAhead)
            {
                snapshot.OpenContractsCounted = counted.Count;
                for (int i = 0; i < counted.Count; i++)
                    if (!string.IsNullOrEmpty(counted[i].Key))
                        snapshot.CountedContractKeys.Add(counted[i].Key);
            }
            ParsekLog.VerboseOnChange(Tag, "click-reserve-snapshot", snapshot.Describe(),
                "Click reservation (a committed cost lies ahead): " + snapshot.Describe());
            return snapshot;
        }

        /// <summary>True when some committed cost-type row of any pool lies after <paramref name="now"/>.</summary>
        internal static bool HasCostRowAfter(IReadOnlyList<GameAction> els, double now)
        {
            if (els == null) return false;
            for (int i = 0; i < els.Count; i++)
            {
                var a = els[i];
                if (a != null && a.UT > now && ReservedFloorPlan.IsAnyPoolCostRow(a))
                    return true;
            }
            return false;
        }

        private static RecalculationEngine.ClickReservationProbe ProbeAt(IReadOnlyList<GameAction> els, double now)
        {
            int ledgerVersion = Ledger.StateVersion;
            lock (syncRoot)
            {
                if (cachedProbe != null && ReferenceEquals(cachedEls, els) && cachedLedgerVersion == ledgerVersion
                    && now >= cachedFromUT && now < cachedUntilUT)
                    return cachedProbe;
            }

            var list = new List<GameAction>(els);
            var probe = RecalculationEngine.ProbeClickReservationAtCutoff(list, now);
            // The probe holds until the clock reaches the next row after now.
            double until = double.PositiveInfinity;
            for (int i = 0; i < list.Count; i++)
                if (list[i] != null && list[i].UT > now && list[i].UT < until)
                    until = list[i].UT;
            lock (syncRoot)
            {
                cachedProbe = probe;
                cachedEls = els;
                cachedLedgerVersion = ledgerVersion;
                cachedFromUT = now;
                cachedUntilUT = until;
                ProbeCount++;
            }
            var parts = new List<string>(probe.Pools.Count);
            for (int i = 0; i < probe.Pools.Count; i++)
                parts.Add(probe.Pools[i].Pool + "(ahead=" + probe.Pools[i].CostRowsAhead.ToString(IC)
                    + " need=" + probe.Pools[i].Need.ToString("R", IC) + ")");
            string state = string.Join(" ", parts.ToArray());
            ParsekLog.VerboseOnChange(Tag, "click-reserve-probe", state,
                "Click reservation probe at ut=" + now.ToString("R", IC) + " rows=" + list.Count.ToString(IC)
                + " validUntil=" + until.ToString("R", IC) + ": " + state);
            return probe;
        }

        /// <summary>Drops the cached probe (the next snapshot re-probes).</summary>
        internal static void Invalidate()
        {
            lock (syncRoot)
            {
                cachedProbe = null;
                cachedEls = null;
            }
        }

        // ------------------------------------------------------------ decisions

        /// <summary>
        /// The purchase decision at now, logged: the one call behind every purchase mark
        /// and backstop. <paramref name="what"/> names the purchase in the log.
        /// </summary>
        internal static PurchaseRefusal EvaluatePurchaseNow(StockPool pool, double cost, string what)
        {
            if (GameStateRecorder.IsReplayingActions) return new PurchaseRefusal { Pool = pool, Cost = cost };
            var snapshot = Current();
            var r = ClickReservation.EvaluatePurchase(snapshot, pool, cost);
            LogPurchase(r, snapshot, what);
            return r;
        }

        /// <summary>A strategy's setup over its three costs, at now, logged.</summary>
        internal static PurchaseRefusal EvaluatePurchaseNow(double funds, double science, double reputation, string what)
        {
            if (GameStateRecorder.IsReplayingActions) return new PurchaseRefusal { Pool = StockPool.Funds };
            var snapshot = Current();
            var r = ClickReservation.EvaluatePurchase(snapshot, funds, science, reputation);
            LogPurchase(r, snapshot, what);
            return r;
        }

        private static void LogPurchase(PurchaseRefusal r, ClickReservationSnapshot snapshot, string what)
        {
            if (!snapshot.AnyCostAhead) return;
            string line = (what ?? "purchase") + " pool=" + r.Pool + " cost=" + r.Cost.ToString("R", IC)
                + " charged=" + r.Charged.ToString("R", IC)
                + " spendable=" + r.Spendable.ToString("R", IC) + " refused=" + (r.Refused ? "true" : "false");
            if (r.Refused)
                ParsekLog.InfoRateLimited(Tag, "purchase-refused|" + (what ?? ""),
                    "Purchase refused, reserved for later costs: " + line + " " + snapshot.Describe());
            else
                ParsekLog.VerboseRateLimited(Tag, "purchase-allowed|" + (what ?? ""),
                    "Purchase allowed under the click reservation: " + line);
        }

        /// <summary>
        /// The purchase backstop: refuses (true) and shows the refused-click dialog when
        /// <see cref="EvaluatePurchaseNow(StockPool, double, string)"/> refuses, the same
        /// decision the greyed control reads (<see cref="LayerPurchase"/>).
        /// </summary>
        internal static bool TryRefusePurchase(StockPool pool, double cost, string what, string dialogTitle)
        {
            PurchaseRefusal r = EvaluatePurchaseNow(pool, cost, what);
            if (!r.Refused) return false;
            ShowRefused(r, dialogTitle);
            return true;
        }

        /// <summary>The refused-click dialog of a purchase: the shared sentence and its numbers.</summary>
        internal static void ShowRefused(PurchaseRefusal r, string dialogTitle)
        {
            CommittedActionDialog.ShowBlocked(dialogTitle ?? "Cannot buy",
                ReservationExplanation.ClickReserve().Body,
                ReservationExplanation.ClickReservePurchaseDetail(r.Pool, ChargedOf(r), r.Spendable));
        }

        /// <summary>
        /// The purchase mark: a decoration no committed row blocks becomes a
        /// <see cref="StockUiDecorationKind.ContractReserve"/> block (marked by the greyed
        /// control's reason) when the click reservation refuses the purchase. A decoration a
        /// committed row already blocks keeps its own reason.
        /// </summary>
        internal static StockUiDecoration LayerPurchase(StockUiDecoration d, StockPool pool, double cost, string what)
        {
            if (d.Blocked) return d;
            PurchaseRefusal r = EvaluatePurchaseNow(pool, cost, what);
            return LayerPurchase(d, r);
        }

        /// <summary>Pure form of <see cref="LayerPurchase(StockUiDecoration, StockPool, double, string)"/>.</summary>
        internal static StockUiDecoration LayerPurchase(StockUiDecoration d, PurchaseRefusal r)
        {
            if (d.Blocked || !r.Refused) return d;
            d.Kind = StockUiDecorationKind.ContractReserve;
            d.Marked = true;
            d.Blocked = true;
            d.Why = PurchaseReason(r);
            d.Title = ReservationExplanation.ClickReserve().Title;
            d.UT = double.NaN;
            return d;
        }

        /// <summary>The reason for a refused purchase: the shared sentence and its numbers.</summary>
        internal static string PurchaseReason(PurchaseRefusal r)
        {
            return ReservationExplanation.ClickReserve().Body + " "
                + ReservationExplanation.ClickReservePurchaseDetail(r.Pool, ChargedOf(r), r.Spendable);
        }

        /// <summary>The amount a refusal names: what the click would take (post-curve for
        /// reputation), the cost when a caller built the refusal without it.</summary>
        internal static double ChargedOf(PurchaseRefusal r)
        {
            return r.Charged > 0.0 ? r.Charged : r.Cost;
        }

        /// <summary>The reason for a refused Mission Control click.</summary>
        internal static string ContractReason(ContractClickRefusal r)
        {
            return ReservationExplanation.ClickReserve().Body + " "
                + ReservationExplanation.ClickReserveShortDetail(r.Pool, r.Shortfall);
        }

        /// <summary>The click-reservation input of one live contract's Mission Control
        /// decision, or null when no committed cost lies ahead (nothing to refuse).</summary>
        internal static ContractReserveInput InputFor(Contract contract, ClickReservationSnapshot snapshot)
        {
            if (contract == null || snapshot == null || !snapshot.AnyCostAhead) return null;
            ContractReserveInput input = InputFor(MoneyOf(contract), snapshot, RepLossDeclined());
            bool accept, decline, cancel;
            StockAllows(contract, out accept, out decline, out cancel);
            input.StockAllowsAccept = accept;
            input.StockAllowsDecline = decline;
            input.StockAllowsCancel = cancel;
            return input;
        }

        /// <summary>
        /// Stock's own verdict on the three Mission Control clicks (KSP 1.12.5, decompiled
        /// <c>MissionControl</c>): Accept while <c>GetActiveContractCount()</c> is under
        /// <c>GetActiveContractsLimit</c> at the Mission Control level (<c>RefreshUIControls</c>),
        /// Decline <c>CanBeDeclined()</c>, Cancel <c>CanBeCancelled()</c>. Contract
        /// Configurator's <c>CanAccept</c> is NOT read here (Parsek postfixes it with this same
        /// decision). An unreadable verdict counts as allowed, so the reservation still applies.
        /// </summary>
        private static void StockAllows(Contract contract, out bool accept, out bool decline, out bool cancel)
        {
            accept = decline = cancel = true;
            try { accept = ReadStockAcceptAllowedCore(); }
            catch (Exception) { accept = true; }
            try { decline = contract.CanBeDeclined(); }
            catch (Exception) { decline = true; }
            try { cancel = contract.CanBeCancelled(); }
            catch (Exception) { cancel = true; }
        }

        [MethodImpl(MethodImplOptions.NoInlining)]
        private static bool ReadStockAcceptAllowedCore()
        {
            if (GameVariables.Instance == null || ContractSystem.Instance == null) return true;
            int limit = GameVariables.Instance.GetActiveContractsLimit(
                ScenarioUpgradeableFacilities.GetFacilityLevel(SpaceCenterFacility.MissionControl));
            return ContractSystem.Instance.GetActiveContractCount() < limit;
        }

        /// <summary>Pure form of <see cref="InputFor(Contract, ClickReservationSnapshot)"/>.</summary>
        internal static ContractReserveInput InputFor(ContractMoney money, ClickReservationSnapshot snapshot,
            double repLossDeclined)
        {
            if (snapshot == null) return null;
            double rep = double.IsNaN(snapshot.Reputation.Live) ? snapshot.Reputation.Target : snapshot.Reputation.Live;
            return new ContractReserveInput
            {
                Snapshot = snapshot,
                Money = money,
                RepLossDeclined = repLossDeclined,
                CurrentReputation = double.IsNaN(rep) || double.IsInfinity(rep) ? 0.0 : rep,
            };
        }

        /// <summary>The live <c>RepLossDeclined</c> (stock career parameter).</summary>
        internal static double RepLossDeclined()
        {
            var seam = RepLossDeclinedProviderForTesting;
            if (seam != null) return seam();
            try { return ReadRepLossDeclinedCore(); }
            catch (Exception) { return 0.0; }
        }

        /// <summary>The money of a live contract.</summary>
        internal static ContractMoney MoneyOf(Contract contract)
        {
            if (contract == null) return default(ContractMoney);
            try { return ReadMoneyCore(contract); }
            catch (Exception) { return default(ContractMoney); }
        }

        [MethodImpl(MethodImplOptions.NoInlining)]
        private static ContractMoney ReadMoneyCore(Contract c)
        {
            return new ContractMoney
            {
                FundsAdvance = c.FundsAdvance,
                FundsFailure = c.FundsFailure,
                ReputationFailure = c.ReputationFailure,
                DateAccepted = c.DateAccepted,
                DateDeadline = c.DateDeadline,
            };
        }

        [MethodImpl(MethodImplOptions.NoInlining)]
        private static double ReadRepLossDeclinedCore()
        {
            var game = HighLogic.CurrentGame;
            if (game == null || game.Parameters == null || game.Parameters.Career == null) return 0.0;
            return game.Parameters.Career.RepLossDeclined;
        }

        private static double ReadLive(StockPool pool)
        {
            var seam = LivePoolProviderForTesting;
            if (seam != null) return seam(pool);
            try { return ReadLiveCore(pool); }
            catch (Exception) { return double.NaN; }
        }

        [MethodImpl(MethodImplOptions.NoInlining)]
        private static double ReadLiveCore(StockPool pool)
        {
            switch (pool)
            {
                case StockPool.Science:
                    return ResearchAndDevelopment.Instance != null ? ResearchAndDevelopment.Instance.Science : double.NaN;
                case StockPool.Reputation:
                    return Reputation.Instance != null ? Reputation.Instance.reputation : double.NaN;
                default:
                    return Funding.Instance != null ? Funding.Instance.Funds : double.NaN;
            }
        }

        private static IList<OpenContractPenalty> ReadOpenContracts()
        {
            var seam = OpenContractsProviderForTesting;
            if (seam != null) return seam();
            try { return ReadOpenContractsCore(); }
            catch (Exception) { return null; }
        }

        [MethodImpl(MethodImplOptions.NoInlining)]
        private static IList<OpenContractPenalty> ReadOpenContractsCore()
        {
            var list = new List<OpenContractPenalty>();
            var system = ContractSystem.Instance;
            if (system == null || system.Contracts == null) return list;
            for (int i = 0; i < system.Contracts.Count; i++)
            {
                Contract c = system.Contracts[i];
                if (c == null || c.ContractState != Contract.State.Active) continue;
                list.Add(new OpenContractPenalty
                {
                    Key = c.ContractGuid.ToString(),
                    Funds = c.FundsFailure,
                    ReputationNominal = c.ReputationFailure,
                });
            }
            return list;
        }

        internal static void ResetForTesting()
        {
            LivePoolProviderForTesting = null;
            OpenContractsProviderForTesting = null;
            RepLossDeclinedProviderForTesting = null;
            SnapshotFaultForTesting = null;
            Invalidate();
            lock (syncRoot)
            {
                cachedLedgerVersion = int.MinValue;
                cachedFromUT = double.NaN;
                cachedUntilUT = double.NaN;
                ProbeCount = 0;
            }
        }
    }
}
