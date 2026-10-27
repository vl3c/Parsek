using System;
using System.Collections.Generic;
using System.Globalization;

namespace Parsek
{
    /// <summary>
    /// Income with no ledger channel is live-only (design 3.15 step 9, owner ruling
    /// 2026-10-26). An earning stock applied under a reason no Parsek channel converts (the
    /// Alt+F12 cheat menu, a Making History mission reward, a mod's own reason) is filed as an
    /// untagged row at its own UT naming that reason (<see cref="GameAction.IncomeReason"/>),
    /// instead of reaching the ledger later as a <see cref="GameActionType.StockPoolAdjustment"/>
    /// dated at the next update. It counts on the timeline it was earned on; a time-travel load
    /// to before it drops it. A mod whose state rolled back with the save then pays it again
    /// once; a mod whose state does not roll back loses it.
    ///
    /// <para>Positive evidence only. <c>TransactionReasons.None</c> is NOT income: it is the
    /// reason Parsek's own pool writes (<see cref="KspStatePatcher"/>) and other mods' pool
    /// corrections use, so it stays on the adjustment path. A reason that names any converted
    /// stock channel (contracts, vessels, strategies, science, structures, R&amp;D, crew,
    /// progression) is never income either, even from a mod: the ledger cannot tell a mod's
    /// payment under such a reason from stock's. A loss is never income. Pool differences an
    /// older save brings stay adjustment rows (3.13).</para>
    /// </summary>
    internal static class LiveOnlyIncome
    {
        internal const string Tag = "LiveOnlyIncome";

        /// <summary>Below this an earning is not filed (pool noise).</summary>
        internal const double MinAmount = 1e-3;

        private static readonly CultureInfo IC = CultureInfo.InvariantCulture;

        // Every stock reason bit some Parsek capture channel converts. Cheating and Mission are
        // the two named reasons no channel converts.
        private const int ConvertedChannelBits =
            (int)(TransactionReasons.ContractAdvance | TransactionReasons.ContractReward
                | TransactionReasons.ContractPenalty | TransactionReasons.VesselRollout
                | TransactionReasons.VesselRecovery | TransactionReasons.VesselLoss
                | TransactionReasons.StrategyInput | TransactionReasons.StrategyOutput
                | TransactionReasons.StrategySetup | TransactionReasons.ScienceTransmission
                | TransactionReasons.StructureRepair | TransactionReasons.StructureCollapse
                | TransactionReasons.StructureConstruction | TransactionReasons.RnDTechResearch
                | TransactionReasons.RnDPartPurchase | TransactionReasons.CrewRecruited
                | TransactionReasons.ContractDecline | TransactionReasons.Progression);

        /// <summary>
        /// Pure: an earning under <paramref name="reason"/> has no ledger channel. True for
        /// <c>Cheating</c>, <c>Mission</c>, <c>Any</c> and any value whose bits name no
        /// converted channel (a mod's own reason); false for <c>None</c> and for any value
        /// with a converted channel's bit.
        /// </summary>
        internal static bool IsUnconvertedReason(TransactionReasons reason)
        {
            if (reason == TransactionReasons.None)
                return false;
            if (reason == TransactionReasons.Any)
                return true;
            return ((int)reason & ConvertedChannelBits) == 0;
        }

        /// <summary>
        /// Pure: the live-only rows for one stock currency query: one per currency the query
        /// pays a positive input of at least <see cref="MinAmount"/>, at the gross input (a
        /// strategy's diversion, if any, is its own conversion leg). Empty for a reason with a
        /// channel. Funds: <c>FundsEarning</c> / <c>Other</c>; reputation: a nominal
        /// <c>ReputationEarning</c> / <c>Other</c> (the walk runs stock's curve); science: an
        /// uncapped, subject-less <c>ScienceLabEarning</c>.
        /// </summary>
        internal static List<GameAction> BuildRows(TransactionReasons reason, StrategyConversionQuery query, double ut)
        {
            var rows = new List<GameAction>();
            if (!IsUnconvertedReason(reason))
                return rows;
            string reasonName = reason.ToString();
            if (query.InputFunds >= MinAmount)
                rows.Add(new GameAction
                {
                    UT = ut,
                    Type = GameActionType.FundsEarning,
                    FundsAwarded = (float)query.InputFunds,
                    FundsSource = FundsEarningSource.Other,
                    IncomeReason = reasonName
                });
            if (query.InputScience >= MinAmount)
                rows.Add(new GameAction
                {
                    UT = ut,
                    Type = GameActionType.ScienceLabEarning,
                    ScienceAwarded = (float)query.InputScience,
                    IncomeReason = reasonName
                });
            if (query.InputReputation >= MinAmount)
                rows.Add(new GameAction
                {
                    UT = ut,
                    Type = GameActionType.ReputationEarning,
                    NominalRep = (float)query.InputReputation,
                    RepSource = ReputationSource.Other,
                    IncomeReason = reasonName
                });
            return rows;
        }

        /// <summary>
        /// Called by the recorder's currency-modified door (after its replay stand-down):
        /// files the live-only rows of one query, if any. Logs one Verbose line for an
        /// unconverted reason that paid nothing positive.
        /// </summary>
        internal static void Capture(TransactionReasons reason, StrategyConversionQuery query, double ut)
        {
            if (!IsUnconvertedReason(reason))
                return;
            List<GameAction> rows = BuildRows(reason, query, ut);
            if (rows.Count == 0)
            {
                ParsekLog.VerboseRateLimited(Tag, "no-income-" + reason,
                    $"Unconverted reason {reason} moved no positive amount - nothing filed " +
                    "(a loss stays on the pool adjustment path)");
                return;
            }
            LedgerOrchestrator.OnLiveOnlyIncome(ut, rows, reason.ToString());
        }

        /// <summary>Pure: a row a time-travel load to <paramref name="loadedUT"/> drops.</summary>
        internal static bool ShouldDrop(GameAction row, double loadedUT)
        {
            return row != null && row.IsLiveOnlyIncome && row.UT > loadedUT;
        }

        /// <summary>
        /// After a time-travel load (<see cref="StockShadowTakeBack.IsTimeTravelLoad"/>):
        /// removes every live-only income row dated after the loaded UT from the ledger and
        /// from the stock pool reference's counted rows (so no update carries it back as a
        /// removed row's adjustment). Returns how many rows it dropped. Any other load kind,
        /// or an unknown loaded UT, drops nothing.
        /// </summary>
        internal static int DropAfterTimeTravelLoad(LoadKind kind, double loadedUT)
            => DropAfterTimeTravelLoadCore(kind, loadedUT);

        /// <summary>
        /// The Re-Fly start's second drop: <c>RewindInvoker.ConsumePostLoad</c> replaces the
        /// whole ledger with the pre-invoke reconciliation bundle AFTER OnLoad's in-session drop
        /// ran, and that capture still holds the income earned after the rewind point, so the
        /// rule runs again on the restored ledger. The UT is the loaded quicksave's own clock
        /// (<paramref name="loadedSaveUT"/>, the in-session drop's UT), else
        /// <paramref name="fallbackUT"/> (the live clock just after the load, else the rewind
        /// point's UT).
        /// </summary>
        internal static int DropAfterReFlyBundleRestore(double loadedSaveUT, double fallbackUT)
        {
            double ut = ResolveReFlyDropUT(loadedSaveUT, fallbackUT);
            ParsekLog.Verbose(Tag,
                $"Re-Fly bundle restore: re-applying the live-only income drop at ut={ut.ToString("R", IC)} " +
                $"(loadedSaveUT={loadedSaveUT.ToString("R", IC)} fallbackUT={fallbackUT.ToString("R", IC)})");
            return DropAfterTimeTravelLoadCore(LoadKind.ReFlyStart, ut);
        }

        /// <summary>Pure: the UT the Re-Fly bundle restore drops income after.</summary>
        internal static double ResolveReFlyDropUT(double loadedSaveUT, double fallbackUT)
        {
            if (!double.IsNaN(loadedSaveUT) && !double.IsInfinity(loadedSaveUT))
                return loadedSaveUT;
            return fallbackUT;
        }

        private static int DropAfterTimeTravelLoadCore(LoadKind kind, double loadedUT)
        {
            if (!StockShadowTakeBack.IsTimeTravelLoad(kind))
                return 0;
            if (double.IsNaN(loadedUT) || double.IsInfinity(loadedUT))
            {
                ParsekLog.Warn(Tag,
                    $"Live-only income not dropped for load kind={kind}: the loaded UT is unknown " +
                    "(any income after it stays on the ledger)");
                return 0;
            }

            List<GameAction> removed = Ledger.RemoveWhere(row => ShouldDrop(row, loadedUT), "live-only income");
            var dropped = new List<string>();
            for (int i = 0; i < removed.Count; i++)
            {
                GameAction row = removed[i];
                if (!string.IsNullOrEmpty(row.ActionId))
                    dropped.Add(row.ActionId);
                if (i < 20)
                    ParsekLog.Verbose(Tag,
                        $"Dropped live-only income {row.Type} id={row.ActionId ?? "(none)"} {FormatAmount(row)} " +
                        $"reason={row.IncomeReason} ut={row.UT.ToString("R", IC)} (after the loaded UT)");
            }
            int forgotten = StockPoolReference.ForgetCounted(dropped);
            if (removed.Count > 0)
                ParsekLog.Info(Tag,
                    $"Time-travel load kind={kind} loadedUT={loadedUT.ToString("R", IC)}: dropped " +
                    $"{removed.Count.ToString(IC)} live-only income row(s) earned after it " +
                    $"(reference rows forgotten={forgotten.ToString(IC)})");
            else
                ParsekLog.Verbose(Tag,
                    $"Time-travel load kind={kind} loadedUT={loadedUT.ToString("R", IC)}: no live-only income after it");
            return removed.Count;
        }

        /// <summary>Pure: the amount text of a live-only row, e.g. <c>funds=500</c>.</summary>
        internal static string FormatAmount(GameAction row)
        {
            if (row == null)
                return "";
            switch (row.Type)
            {
                case GameActionType.FundsEarning: return "funds=" + row.FundsAwarded.ToString("R", IC);
                case GameActionType.ScienceLabEarning: return "science=" + row.ScienceAwarded.ToString("R", IC);
                case GameActionType.ReputationEarning: return "nominalRep=" + row.NominalRep.ToString("R", IC);
                default: return row.Type.ToString();
            }
        }
    }
}
