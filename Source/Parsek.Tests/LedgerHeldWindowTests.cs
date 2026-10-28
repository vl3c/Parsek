using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using Parsek.Patches;
using Xunit;

namespace Parsek.Tests
{
    /// <summary>
    /// REWIND-LEAD-WINDOW-KSC-ACTIONS-LAND-IN-THE-FUTURE: a Rewind-to-Launch winds the clock back
    /// 15 s from the rewind save's own UT, and the rows in between are already in the loaded world.
    /// <see cref="LedgerHeldWindow"/> holds them as reached until the clock passes the save UT.
    /// The numbers are RW-9's (runs 2026-10-28_1522 / _1524 / _1535): the rewind save at
    /// 185800.285736677, the clock wound back to 185785.3, the strategy activation at
    /// 185790.02069345751, the R&amp;D annex demolition at 185790.06069345749, the committed
    /// repair at 185899.9.
    /// </summary>
    [Collection("Sequential")]
    public class LedgerHeldWindowTests : IDisposable
    {
        private const double SaveUT = 185800.285736677;
        private const double WoundBackUT = 185785.285736677;
        private const double StrategyUT = 185790.02069345751;
        private const double DemolishUT = 185790.06069345749;
        private const double RepairUT = 185899.9;
        private const string AnnexId = "SpaceCenter/ResearchAndDevelopment/Facility/ForeverAlone";
        private const string RndId = "SpaceCenter/ResearchAndDevelopment";

        private readonly List<string> logLines = new List<string>();
        private readonly List<(string title, string reason)> dialogs = new List<(string, string)>();

        public LedgerHeldWindowTests()
        {
            ParsekLog.ResetTestOverrides();
            ParsekLog.VerboseOverrideForTesting = true;
            ParsekLog.TestSinkForTesting = line => logLines.Add(line);
            RecordingStore.SuppressLogging = true;
            GameStateStore.SuppressLogging = true;
            RecordingStore.ResetForTesting();
            GameStateStore.ResetForTesting();
            MilestoneStore.ResetForTesting();
            KspStatePatcher.SuppressUnityCallsForTesting = true;
            LedgerOrchestrator.ResetForTesting();
            GameStateRecorder.ResetForTesting();
            EffectiveState.ResetCachesForTesting();
            RewindContext.ResetForTesting();
            ParsekScenario.ResetInstanceForTesting();
            CommittedFutureIndexCache.ResetForTesting();
            LedgerHeldWindow.ResetForTesting();
            FacilityDisplayNames.FacilityNameLookupForTesting = id => null;
            CommittedActionDialog.TestHookForTesting = (title, reason, detail) => dialogs.Add((title, reason));
        }

        public void Dispose()
        {
            CommittedActionDialog.TestHookForTesting = null;
            FacilityDisplayNames.FacilityNameLookupForTesting = null;
            ParsekScenario.CurrentTimelineUTProviderForTesting = null;
            LedgerHeldWindow.ResetForTesting();
            CommittedFutureIndexCache.ResetForTesting();
            LedgerOrchestrator.ResetForTesting();
            KspStatePatcher.ResetForTesting();
            GameStateRecorder.ResetForTesting();
            EffectiveState.ResetCachesForTesting();
            RecordingStore.ResetForTesting();
            RecordingStore.SuppressLogging = false;
            GameStateStore.ResetForTesting();
            MilestoneStore.ResetForTesting();
            RewindContext.ResetForTesting();
            ParsekScenario.ResetInstanceForTesting();
            ParsekLog.ResetTestOverrides();
            ParsekLog.SuppressLogging = true;
        }

        private static GameAction FundsSeed(float amount) =>
            new GameAction { UT = 0.0, Type = GameActionType.FundsInitial, InitialFunds = amount };

        private static GameAction Spend(double ut, float amount, string key) =>
            new GameAction
            {
                UT = ut,
                Type = GameActionType.FundsSpending,
                FundsSpendingSource = FundsSpendingSource.Other,
                DedupKey = key,
                FundsSpent = amount
            };

        private static GameAction Strategy(double ut, float repCost) =>
            new GameAction
            {
                UT = ut,
                Type = GameActionType.StrategyActivate,
                StrategyId = "AggressiveNegotiations",
                SetupReputationCost = repCost
            };

        private static GameAction Demolish(double ut) =>
            new GameAction { UT = ut, Type = GameActionType.FacilityDestruction, FacilityId = AnnexId };

        private static GameAction Repair(double ut, float cost) =>
            new GameAction { UT = ut, Type = GameActionType.FacilityRepair, FacilityId = AnnexId, FacilityCost = cost };

        private static FacilityRepairCapture.BuildingRepairInput[] AnnexDestroyed() =>
            new[]
            {
                new FacilityRepairCapture.BuildingRepairInput
                {
                    BuildingId = AnnexId, IsDestroyed = true, RepairCost = 2000f
                }
            };

        private static void AddAll(params GameAction[] actions)
        {
            for (int i = 0; i < actions.Length; i++)
                Ledger.AddAction(actions[i]);
        }

        // ================================================================
        // Pure rules
        // ================================================================

        [Fact]
        public void ApplyHeldFloor_RaisesAClockBehindIt_LeavesEveryOtherReadingAlone()
        {
            Assert.Equal(SaveUT, LedgerHeldWindow.ApplyHeldFloor(WoundBackUT, SaveUT));
            Assert.Equal(SaveUT, LedgerHeldWindow.ApplyHeldFloor(SaveUT, SaveUT));
            Assert.Equal(RepairUT, LedgerHeldWindow.ApplyHeldFloor(RepairUT, SaveUT));
            // No floor.
            Assert.Equal(WoundBackUT, LedgerHeldWindow.ApplyHeldFloor(WoundBackUT, double.NaN));
            Assert.Equal(WoundBackUT, LedgerHeldWindow.ApplyHeldFloor(WoundBackUT, 0.0));
            // A clock that is unknown, not ready, or "no Planetarium" is not raised.
            Assert.True(double.IsNaN(LedgerHeldWindow.ApplyHeldFloor(double.NaN, SaveUT)));
            Assert.Equal(0.0, LedgerHeldWindow.ApplyHeldFloor(0.0, SaveUT));
            Assert.Equal(double.MaxValue, LedgerHeldWindow.ApplyHeldFloor(double.MaxValue, SaveUT));
        }

        [Fact]
        public void ResolveLedgerCutoffUT_RaisesACutoff_KeepsAFullWalkFull()
        {
            LedgerHeldWindow.SetForTesting(SaveUT);
            Assert.Equal(SaveUT, LedgerHeldWindow.ResolveLedgerCutoffUT(WoundBackUT));
            Assert.Equal(RepairUT, LedgerHeldWindow.ResolveLedgerCutoffUT(RepairUT));
            Assert.Null(LedgerHeldWindow.ResolveLedgerCutoffUT(null));
        }

        [Fact]
        public void ResolveAtRewind_IsTheSaveUT_OnlyWhenTheClockWasWoundBack()
        {
            Assert.Equal(SaveUT, LedgerHeldWindow.ResolveAtRewind(WoundBackUT, SaveUT, double.NaN));
            // A rewind save written inside an earlier window holds the rows up to its carried floor.
            Assert.Equal(185900.0, LedgerHeldWindow.ResolveAtRewind(WoundBackUT, SaveUT, 185900.0));
            // A carried floor the save's own clock already passed is no raise.
            Assert.Equal(SaveUT, LedgerHeldWindow.ResolveAtRewind(WoundBackUT, SaveUT, 1000.0));
            // No windback (Warp-to-game-start, a lead clamped away): no floor.
            Assert.True(double.IsNaN(LedgerHeldWindow.ResolveAtRewind(SaveUT, SaveUT, double.NaN)));
            // Unknown UTs: no floor, rows after the clock stay committed future.
            Assert.True(double.IsNaN(LedgerHeldWindow.ResolveAtRewind(WoundBackUT, double.NaN, double.NaN)));
            Assert.True(double.IsNaN(LedgerHeldWindow.ResolveAtRewind(double.NaN, SaveUT, double.NaN)));
        }

        [Fact]
        public void ResolveAfterLoad_KeepsTheCarriedFloor_OnlyWhileTheLoadedClockIsBehindIt()
        {
            // A quicksave written in the window.
            Assert.Equal(SaveUT, LedgerHeldWindow.ResolveAfterLoad(185788.0, SaveUT));
            // A save written after the clock passed it.
            Assert.True(double.IsNaN(LedgerHeldWindow.ResolveAfterLoad(185850.0, SaveUT)));
            Assert.True(double.IsNaN(LedgerHeldWindow.ResolveAfterLoad(SaveUT, SaveUT)));
            // A save without one, or an unknown clock.
            Assert.True(double.IsNaN(LedgerHeldWindow.ResolveAfterLoad(185788.0, double.NaN)));
            Assert.True(double.IsNaN(LedgerHeldWindow.ResolveAfterLoad(double.NaN, SaveUT)));
        }

        // ================================================================
        // Lifecycle, persistence and logging
        // ================================================================

        [Fact]
        public void SetAtRewind_SetsAndLogs_InstallAfterLoad_ReplacesIt()
        {
            LedgerHeldWindow.SetAtRewind(WoundBackUT, SaveUT, double.NaN);
            Assert.Equal(SaveUT, LedgerHeldWindow.HeldThroughUT);
            Assert.Contains(logLines, l => l.Contains("[" + LedgerHeldWindow.Tag + "]")
                && l.Contains("Rewind: ledger held-through UT none -> " + SaveUT.ToString("R", CultureInfo.InvariantCulture))
                && l.Contains("count as reached until the clock passes it"));

            // A load of a save written before the rewind (it carries none) drops the floor.
            LedgerHeldWindow.InstallAfterLoad(185000.0, double.NaN, "Load (Cold)");
            Assert.False(LedgerHeldWindow.IsSet);
            Assert.Contains(logLines, l => l.Contains("Load (Cold): ledger held-through UT "
                + SaveUT.ToString("R", CultureInfo.InvariantCulture) + " -> none"));
        }

        [Fact]
        public void Save_IsSparse_AndRoundTripsInvariantly()
        {
            var empty = new ConfigNode("SCENARIO");
            LedgerHeldWindow.SaveInto(empty);
            Assert.False(empty.HasValue(LedgerHeldWindow.ValueName));

            LedgerHeldWindow.SetForTesting(SaveUT);
            var node = new ConfigNode("SCENARIO");
            node.AddValue(LedgerHeldWindow.ValueName, "1");
            LedgerHeldWindow.SaveInto(node);
            Assert.Single(node.GetValues(LedgerHeldWindow.ValueName));
            Assert.Equal(SaveUT.ToString("R", CultureInfo.InvariantCulture), node.GetValue(LedgerHeldWindow.ValueName));
            Assert.Equal(SaveUT, LedgerHeldWindow.ReadSaved(node));

            var bad = new ConfigNode("SCENARIO");
            bad.AddValue(LedgerHeldWindow.ValueName, "not-a-ut");
            Assert.True(double.IsNaN(LedgerHeldWindow.ReadSaved(bad)));
            Assert.True(double.IsNaN(LedgerHeldWindow.ReadSaved(null)));
        }

        [Fact]
        public void ExpireIfClockPassed_DropsTheFloorOnceReached_AndLogs()
        {
            LedgerHeldWindow.SetForTesting(SaveUT);
            Assert.False(LedgerHeldWindow.ExpireIfClockPassed(185799.0, "ksc-clock"));
            Assert.True(LedgerHeldWindow.IsSet);
            Assert.True(LedgerHeldWindow.ExpireIfClockPassed(185801.0, "ksc-clock"));
            Assert.False(LedgerHeldWindow.IsSet);
            Assert.Contains(logLines, l => l.Contains("[" + LedgerHeldWindow.Tag + "]")
                && l.Contains("ksc-clock: the clock 185801") && l.Contains("floor expired"));
        }

        [Fact]
        public void LoadPolicy_EveryLoadTakesTheSavesCopy_ExceptAPlainRewind()
        {
            foreach (LoadKind kind in Enum.GetValues(typeof(LoadKind)))
            {
                var d = LoadReconcilePolicy.Decide(kind, LoadStateCategory.LedgerHeldWindow);
                Assert.Equal(kind == LoadKind.PlainRewind ? LoadReconcileAction.Clear : LoadReconcileAction.Save, d.Action);
                Assert.False(d.IsKnownGap);
            }
            Assert.True(LoadReconcilePolicy.IsConsumedEarly(LoadStateCategory.LedgerHeldWindow));
            Assert.Equal(LoadReconcileAction.Save,
                LoadReconcilePolicy.DecideEarly(EarlyLoadKind.InSession, LoadStateCategory.LedgerHeldWindow).Action);
        }

        [Fact]
        public void DiscardReFlySplice_KeepsTheRewindPointsOwnHeldThroughUT()
        {
            var target = new ConfigNode("SCENARIO");
            target.AddValue("name", "ParsekScenario");
            target.AddValue(LedgerHeldWindow.ValueName, SaveUT.ToString("R", CultureInfo.InvariantCulture));
            var live = new ConfigNode("SCENARIO");
            live.AddValue("name", "ParsekScenario");
            live.AddValue(LedgerHeldWindow.ValueName, "185950");

            Assert.Null(RevertInterceptor.SpliceLiveParsekScenarioNode(target, live));
            Assert.Single(target.GetValues(LedgerHeldWindow.ValueName));
            Assert.Equal(SaveUT, LedgerHeldWindow.ReadSaved(target));

            // A rewind point written outside any window carries none, whatever the session's says.
            var plain = new ConfigNode("SCENARIO");
            plain.AddValue("name", "ParsekScenario");
            Assert.Null(RevertInterceptor.SpliceLiveParsekScenarioNode(plain, live));
            Assert.False(plain.HasValue(LedgerHeldWindow.ValueName));
        }

        // ================================================================
        // The "now" every consumer reads
        // ================================================================

        [Fact]
        public void CommittedFutureNow_AndLedgerClock_ReadTheFloor()
        {
            CommittedFutureIndexCache.NowUtProviderForTesting = () => WoundBackUT;
            ParsekScenario.CurrentTimelineUTProviderForTesting = () => WoundBackUT;
            Assert.Equal(WoundBackUT, CommittedFutureIndexCache.CurrentUT());
            Assert.Equal(WoundBackUT, ParsekScenario.GetCurrentTimelineUTForLedgerRecalc());

            LedgerHeldWindow.SetForTesting(SaveUT);
            Assert.Equal(SaveUT, CommittedFutureIndexCache.CurrentUT());
            Assert.Equal(SaveUT, ParsekScenario.GetCurrentTimelineUTForLedgerRecalc());
        }

        [Fact]
        public void KscClockCursor_LooksForTheNextRowAfterTheFloor()
        {
            var asked = new List<double>();
            int recalcs = 0;
            Func<double, double> next = after =>
            {
                asked.Add(after);
                return after < StrategyUT ? StrategyUT : RepairUT;
            };

            var unheld = new CareerLedgerClockCursor("ksc", next, (ut, reason) => recalcs++);
            unheld.Advance(WoundBackUT);
            Assert.True(unheld.Advance(185790.1));
            Assert.Equal(1, recalcs);

            recalcs = 0;
            asked.Clear();
            LedgerHeldWindow.SetForTesting(SaveUT);
            var held = new CareerLedgerClockCursor("ksc", next, (ut, reason) => recalcs++);
            held.Advance(WoundBackUT);
            Assert.False(held.Advance(185790.1));
            Assert.Equal(0, recalcs);
            Assert.All(asked, a => Assert.Equal(SaveUT, a));
            Assert.True(held.Advance(185900.0));
            Assert.Equal(1, recalcs);
        }

        // ================================================================
        // The measured defects
        // ================================================================

        [Fact]
        public void RepairBlock_WithoutTheFloor_AllowsTheSecondPaidRepair_TheRw9Defect()
        {
            AddAll(Demolish(DemolishUT), Repair(RepairUT, 2000f));
            CommittedFutureIndexCache.NowUtProviderForTesting = () => WoundBackUT;

            Assert.False(FacilityRepairBlock.TryBlockFacilityRepairForBuildings(RndId, AnnexDestroyed()));
            Assert.Empty(dialogs);
            Assert.Contains(logLines, l => l.Contains("Allowing facility repair: '" + RndId + "'")
                && l.Contains("no committed future repair covers the destruction"));
        }

        [Fact]
        public void RepairBlock_WithTheFloor_RefusesTheSecondRepair_TheCommittedOneCoversIt()
        {
            AddAll(Demolish(DemolishUT), Repair(RepairUT, 2000f));
            CommittedFutureIndexCache.NowUtProviderForTesting = () => WoundBackUT;
            LedgerHeldWindow.SetAtRewind(WoundBackUT, SaveUT, double.NaN);

            Assert.True(FacilityRepairBlock.TryBlockFacilityRepairForBuildings(RndId, AnnexDestroyed()));
            Assert.Single(dialogs);
            Assert.Contains(logLines, l => l.Contains("Blocking facility repair: '" + RndId + "'")
                && l.Contains("already repaired by a committed future row"));

            // Mirror: past the committed repair the block lifts (a new destruction is the player's).
            CommittedFutureIndexCache.NowUtProviderForTesting = () => 185950.0;
            Assert.False(FacilityRepairBlock.TryBlockFacilityRepairForBuildings(RndId, AnnexDestroyed()));
        }

        [Fact]
        public void RewindWalk_WithoutTheFloor_LeavesTheWindowRowsToCrossAgain_TheRw9Refire()
        {
            AddAll(FundsSeed(155591.16f), Strategy(StrategyUT, 14.5f), Demolish(DemolishUT), Repair(RepairUT, 2000f));
            LedgerOrchestrator.RecalculateAndPatch(WoundBackUT, suppressSuspiciousDrawdownWarnings: true);
            logLines.Clear();

            LedgerOrchestrator.RecalculateAndPatchForClockAdvance(185790.1, "ksc-clock");

            Assert.Contains(logLines, l => l.Contains("[LedgerOrchestrator]")
                && l.Contains("Clock-crossing reached committed-future rows: crossed=2")
                && l.Contains("StrategyActivate@" + StrategyUT.ToString("R", CultureInfo.InvariantCulture))
                && l.Contains("FacilityDestruction@" + DemolishUT.ToString("R", CultureInfo.InvariantCulture)));
        }

        [Fact]
        public void RewindWalk_WithTheFloor_HoldsTheWindowRows_NoCrossingReFires_TheRepairStillCrosses()
        {
            AddAll(FundsSeed(155591.16f), Strategy(StrategyUT, 14.5f), Demolish(DemolishUT), Repair(RepairUT, 2000f));
            LedgerHeldWindow.SetAtRewind(WoundBackUT, SaveUT, double.NaN);

            LedgerOrchestrator.RecalculateAndPatch(WoundBackUT, suppressSuspiciousDrawdownWarnings: true);
            Assert.Contains(logLines, l => l.Contains("RecalculateAndPatch: walk cutoff "
                + WoundBackUT.ToString("R", CultureInfo.InvariantCulture)
                + " raised to the ledger held-through UT " + SaveUT.ToString("R", CultureInfo.InvariantCulture)));
            // The pools read the window as paid: funds unchanged by the repair still ahead.
            Assert.Equal(155591.16, LedgerOrchestrator.Funds.GetRunningBalance(), 1);
            logLines.Clear();

            // The clock reaches the window rows: nothing crosses, nothing applies twice.
            LedgerOrchestrator.RecalculateAndPatchForClockAdvance(185790.1, "ksc-clock");
            Assert.DoesNotContain(logLines, l => l.Contains("Clock-crossing reached"));
            Assert.Equal(155591.16, LedgerOrchestrator.Funds.GetRunningBalance(), 1);

            // Mirror: the committed repair after the save UT is still committed future, and
            // crosses once when the clock reaches it (the floor expires on the way).
            logLines.Clear();
            LedgerOrchestrator.RecalculateAndPatchForClockAdvance(185900.0, "ksc-clock");
            Assert.Contains(logLines, l => l.Contains("Clock-crossing reached committed-future rows: crossed=1")
                && l.Contains("FacilityRepair@185899.9"));
            Assert.DoesNotContain(logLines, l => l.Contains("Clock-crossing reached") && l.Contains("StrategyActivate@"));
            Assert.False(LedgerHeldWindow.IsSet);
            Assert.Equal(155591.16 - 2000.0, LedgerOrchestrator.Funds.GetRunningBalance(), 1);
        }

        [Fact]
        public void RewindWalk_WithTheFloor_ChargesTheWindowCostsOnce()
        {
            // A part bought in the window and one bought after the save UT.
            AddAll(FundsSeed(10000f), Spend(185795.0, 600f, "window-part"), Spend(185850.0, 900f, "later-part"));
            LedgerOrchestrator.RecalculateAndPatch(WoundBackUT, suppressSuspiciousDrawdownWarnings: true);
            Assert.Equal(10000.0, LedgerOrchestrator.Funds.GetRunningBalance(), 1);

            LedgerHeldWindow.SetAtRewind(WoundBackUT, SaveUT, double.NaN);
            LedgerOrchestrator.RecalculateAndPatch(WoundBackUT, suppressSuspiciousDrawdownWarnings: true);
            // The window purchase is in the loaded world; the later one is still ahead.
            Assert.Equal(9400.0, LedgerOrchestrator.Funds.GetRunningBalance(), 1);
        }
    }
}
