using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using Xunit;

namespace Parsek.Tests
{
    /// <summary>
    /// Design 3.15 step 9 (owner ruling 2026-10-26): an earning stock applied under a reason no
    /// ledger channel converts is filed as an untagged row at its own UT naming the reason; it
    /// counts on its timeline and a time-travel load to before it drops it. The reason
    /// <c>None</c> (Parsek's own pool writes) and every reason with a channel stay on the
    /// adjustment path, and an older save's pool difference stays an adjustment row.
    /// </summary>
    [Collection("Sequential")]
    public class LiveOnlyIncomeTests : IDisposable
    {
        private readonly List<string> logLines = new List<string>();

        public LiveOnlyIncomeTests()
        {
            ParsekLog.ResetTestOverrides();
            ParsekLog.TestSinkForTesting = line => logLines.Add(line);
            ParsekLog.VerboseOverrideForTesting = true;
            GameStateStore.SuppressLogging = true;
            KspStatePatcher.SuppressUnityCallsForTesting = true;
            KspStatePatcher.AllowNegativeCurrencyProviderForTesting = () => false;
            GameStateStore.ResetForTesting();
            RecordingStore.ResetForTesting();
            LedgerOrchestrator.ResetForTesting();
            StockPoolReference.ResetForTesting();
            StockShadowTakeBack.ResetForTesting();
        }

        public void Dispose()
        {
            LedgerOrchestrator.ResetForTesting();
            RecordingStore.ResetForTesting();
            KspStatePatcher.ResetForTesting();
            KspStatePatcher.AllowNegativeCurrencyProviderForTesting = null;
            GameStateStore.ResetForTesting();
            StockPoolReference.ResetForTesting();
            StockShadowTakeBack.ResetForTesting();
            RecalculationEngine.ClearModules();
            ParsekLog.ResetTestOverrides();
            ParsekLog.SuppressLogging = true;
        }

        // ------------------------------------------------------------------
        // Which reasons are income
        // ------------------------------------------------------------------

        [Theory]
        [InlineData(TransactionReasons.Cheating, true)]
        [InlineData(TransactionReasons.Mission, true)]
        [InlineData(TransactionReasons.Any, true)]
        [InlineData((TransactionReasons)0x200000, true)] // a mod's own reason bit
        [InlineData(TransactionReasons.Cheating | TransactionReasons.Mission, true)]
        [InlineData(TransactionReasons.None, false)]     // Parsek's own pool writes
        [InlineData(TransactionReasons.ContractReward, false)]
        [InlineData(TransactionReasons.Progression, false)]
        [InlineData(TransactionReasons.VesselRecovery, false)]
        [InlineData(TransactionReasons.ScienceTransmission, false)]
        [InlineData(TransactionReasons.StrategyOutput, false)]
        [InlineData(TransactionReasons.ContractDecline, false)]
        [InlineData(TransactionReasons.Cheating | TransactionReasons.ContractReward, false)]
        public void IsUnconvertedReason_PositiveEvidenceOnly(TransactionReasons reason, bool expected)
        {
            Assert.Equal(expected, LiveOnlyIncome.IsUnconvertedReason(reason));
        }

        // ------------------------------------------------------------------
        // Row shapes
        // ------------------------------------------------------------------

        [Fact]
        public void BuildRows_OneUntaggedRowPerPositiveCurrency_AtTheGrossInput()
        {
            var q = new StrategyConversionQuery { InputFunds = 50000, InputScience = 12.5, InputReputation = 7, DeltaFunds = -10 };
            List<GameAction> rows = LiveOnlyIncome.BuildRows(TransactionReasons.Cheating, q, 321.5);
            Assert.Equal(3, rows.Count);

            GameAction f = rows.Single(r => r.Type == GameActionType.FundsEarning);
            Assert.Equal(50000f, f.FundsAwarded);
            Assert.Equal(FundsEarningSource.Other, f.FundsSource);
            GameAction s = rows.Single(r => r.Type == GameActionType.ScienceLabEarning);
            Assert.Equal(12.5f, s.ScienceAwarded);
            Assert.Null(s.ScanBodyName);
            GameAction r2 = rows.Single(r => r.Type == GameActionType.ReputationEarning);
            Assert.Equal(7f, r2.NominalRep);
            Assert.Equal(ReputationSource.Other, r2.RepSource);
            Assert.All(rows, r =>
            {
                Assert.Equal(321.5, r.UT);
                Assert.Null(r.RecordingId);
                Assert.Equal("Cheating", r.IncomeReason);
                Assert.True(r.IsLiveOnlyIncome);
            });
            Assert.Equal("Income (Cheating) +50000", GameActionDisplay.GetDescription(f, null));
            Assert.Equal("Income (Cheating) +12.5 sci", GameActionDisplay.GetDescription(s, null));
            Assert.Equal("Income (Cheating) +7 rep", GameActionDisplay.GetDescription(r2, null));
        }

        [Fact]
        public void BuildRows_LossesNoiseAndChannelReasons_FileNothing()
        {
            Assert.Empty(LiveOnlyIncome.BuildRows(TransactionReasons.Cheating,
                new StrategyConversionQuery { InputFunds = -5000, InputScience = 0.0001 }, 1));
            Assert.Empty(LiveOnlyIncome.BuildRows(TransactionReasons.ContractReward,
                new StrategyConversionQuery { InputFunds = 5000 }, 1));
            Assert.Empty(LiveOnlyIncome.BuildRows(TransactionReasons.None,
                new StrategyConversionQuery { InputFunds = 5000 }, 1));
        }

        [Fact]
        public void Registry_EachIncomeShape_IsLiveOnlyAndEnforced()
        {
            foreach (GameAction row in LiveOnlyIncome.BuildRows(TransactionReasons.Mission,
                new StrategyConversionQuery { InputFunds = 1, InputScience = 1, InputReputation = 1 }, 1))
            {
                LedgerEventIdentityEntry e = LedgerEventIdentityRegistry.For(row);
                Assert.Equal(LedgerIdentityRule.LiveOnly, e.Rule);
                Assert.True(e.EnforcedToday);
            }
        }

        [Fact]
        public void PostWalkReconcile_SkipsIncomeRows()
        {
            var f = LiveOnlyIncome.BuildRows(TransactionReasons.Cheating,
                new StrategyConversionQuery { InputFunds = 900, InputScience = 3 }, 1);
            foreach (GameAction row in f)
                Assert.False(PostWalkActionReconciler.ClassifyPostWalk(row).Reconcile);
        }

        [Fact]
        public void IncomeReason_RoundTrips_AsAnAdditiveKey()
        {
            GameAction row = LiveOnlyIncome.BuildRows(TransactionReasons.Cheating,
                new StrategyConversionQuery { InputReputation = 4 }, 9)[0];
            var root = new ConfigNode("ROOT");
            row.SerializeInto(root);
            ConfigNode node = root.GetNode("GAME_ACTION");
            Assert.Equal("Cheating", node.GetValue(GameAction.IncomeReasonKey));
            GameAction back = GameAction.DeserializeFrom(node);
            Assert.Equal("Cheating", back.IncomeReason);
            Assert.Equal(4f, back.NominalRep);
        }

        // ------------------------------------------------------------------
        // Filing and dropping
        // ------------------------------------------------------------------

        // catches: channel-less income still filed only as an adjustment at the next update,
        // or filed without a capture number / with a recording tag.
        [Fact]
        public void Capture_FilesStockAppliedUntaggedRows_AtTheEventUT()
        {
            LiveOnlyIncome.Capture(TransactionReasons.Cheating,
                new StrategyConversionQuery { InputFunds = 25000 }, 400.25);
            GameAction row = Ledger.Actions.Single(a => a.IsLiveOnlyIncome);
            Assert.Equal(GameActionType.FundsEarning, row.Type);
            Assert.Equal(400.25, row.UT);
            Assert.Null(row.RecordingId);
            Assert.False(row.ParsekAuthored);
            Assert.True(row.CaptureSequence > 0);
            Assert.Contains(logLines, l => l.Contains("[INFO][LiveOnlyIncome]") && l.Contains("Live-only income filed")
                && l.Contains("funds=25000") && l.Contains("reason=Cheating"));

            logLines.Clear();
            LiveOnlyIncome.Capture(TransactionReasons.ContractReward,
                new StrategyConversionQuery { InputFunds = 25000 }, 401);
            Assert.Single(Ledger.Actions.Where(a => a.IsLiveOnlyIncome));
        }

        // catches: a rewind keeping income earned after the loaded moment, or the next update
        // carrying it back as a removed row's adjustment.
        [Fact]
        public void TimeTravelLoad_DropsIncomeAfterTheLoadedUT_AndTheReferenceForgetsIt()
        {
            LiveOnlyIncome.Capture(TransactionReasons.Cheating, new StrategyConversionQuery { InputFunds = 100 }, 50);
            LiveOnlyIncome.Capture(TransactionReasons.Cheating, new StrategyConversionQuery { InputFunds = 200 }, 150);
            string lateId = Ledger.Actions.Single(a => a.IsLiveOnlyIncome && a.UT == 150).ActionId;
            StockPoolReference.Funds.Counted[lateId] = new CountedRow { Effect = 200, Stepped = 200 };

            Assert.Equal(0, LiveOnlyIncome.DropAfterTimeTravelLoad(LoadKind.InSessionOther, 100));
            Assert.Equal(0, LiveOnlyIncome.DropAfterTimeTravelLoad(LoadKind.Cold, 100));
            Assert.Equal(2, Ledger.Actions.Count(a => a.IsLiveOnlyIncome));

            Assert.Equal(1, LiveOnlyIncome.DropAfterTimeTravelLoad(LoadKind.PlainRewind, 100));
            Assert.Single(Ledger.Actions.Where(a => a.IsLiveOnlyIncome));
            Assert.Equal(50, Ledger.Actions.Single(a => a.IsLiveOnlyIncome).UT);
            Assert.False(StockPoolReference.Funds.Counted.ContainsKey(lateId));
            Assert.Contains(logLines, l => l.Contains("[INFO][LiveOnlyIncome]") && l.Contains("kind=PlainRewind")
                && l.Contains("dropped 1 live-only income row(s)") && l.Contains("forgotten=1"));

            // A row exactly at the loaded UT was in the loaded state: kept.
            Assert.Equal(0, LiveOnlyIncome.DropAfterTimeTravelLoad(LoadKind.StockRevert, 50));
            Assert.Equal(0, LiveOnlyIncome.DropAfterTimeTravelLoad(LoadKind.PlainRewind, double.NaN));
            Assert.Contains(logLines, l => l.Contains("[WARN][LiveOnlyIncome]") && l.Contains("loaded UT is unknown"));
        }

        [Fact]
        public void ShouldDrop_OnlyIncomeRowsAfterTheLoadedUT()
        {
            var income = new GameAction { Type = GameActionType.FundsEarning, UT = 10, IncomeReason = "Cheating" };
            var plain = new GameAction { Type = GameActionType.FundsEarning, UT = 10 };
            Assert.True(LiveOnlyIncome.ShouldDrop(income, 9.9));
            Assert.False(LiveOnlyIncome.ShouldDrop(income, 10));
            Assert.False(LiveOnlyIncome.ShouldDrop(plain, 0));
            Assert.False(LiveOnlyIncome.ShouldDrop(null, 0));
        }

        // ------------------------------------------------------------------
        // The pool: income stepped, not adjusted, and the same end value
        // ------------------------------------------------------------------

        private static double PatchFunds(List<GameAction> ledger, FundsModule funds, double now, double live,
            out StockPoolMeasurement m)
        {
            RecalculationEngine.Recalculate(ledger.ToList(), now);
            var state = StockPoolReference.Funds;
            List<StockPoolRow> rows = StockPoolReference.BuildRows(
                StockPool.Funds, ledger, funds.GetRowEffects(), funds.GetTakeBackDeltas());
            double min = funds.GetProjectionMinBalance();
            m = StockPoolReference.Measure(state, rows, now, live, 0.0, false, 0.01, min,
                new HashSet<string>(ledger.Select(a => a.ActionId), StringComparer.Ordinal), clampAtZero: true);
            if (m.Adjustment != 0.0)
            {
                GameAction row = StockPoolReference.BuildAdjustmentRow(StockPool.Funds, m.Adjustment, now);
                ledger.Add(row);
                m.AdjustmentRow = row;
            }
            double write = m.GapAdopted
                ? live
                : StockPoolReference.ResolveCurrencyWriteTarget(min, funds.GetRunningBalance(), live, false, m.Adjustment);
            StockPoolReference.Commit(state, rows, m, write, 0.0);
            return write;
        }

        // catches: a never-rewound career ending with a different pool once channel-less income
        // is a row instead of an adjustment; the income is no longer an adjustment row.
        [Theory]
        [InlineData(1)]
        [InlineData(2)]
        [InlineData(3)]
        [InlineData(4)]
        [InlineData(5)]
        public void NeverRewound_IncomeRowEndsAtTheSamePoolAsTheOldAdjustment(int seed)
        {
            var rng = new Random(seed);
            double[] incomes = Enumerable.Range(0, 3 + rng.Next(5)).Select(_ => (double)rng.Next(1, 500) * 10).ToArray();

            double oldEnd = RunCareer(incomes, filedAsRows: false, out int oldAdjustments);
            double newEnd = RunCareer(incomes, filedAsRows: true, out int newAdjustments);

            Assert.Equal(oldEnd, newEnd, 6);
            Assert.Equal(incomes.Length, oldAdjustments);
            Assert.Equal(0, newAdjustments);
        }

        private double RunCareer(double[] incomes, bool filedAsRows, out int adjustments)
        {
            StockPoolReference.ResetForTesting();
            var funds = new FundsModule();
            RecalculationEngine.ClearModules();
            RecalculationEngine.RegisterModule(funds, RecalculationEngine.ModuleTier.SecondTier);
            var ledger = new List<GameAction>
            {
                new GameAction { ActionId = "seedF", UT = 0, Type = GameActionType.FundsInitial, InitialFunds = 1000f },
            };
            double live = 1000;
            StockPoolMeasurement m;
            live = PatchFunds(ledger, funds, 1, live, out m);
            double ut = 10;
            long cseq = 100;
            for (int i = 0; i < incomes.Length; i++)
            {
                ut += 10;
                // Stock applied the cheat funds.
                live += incomes[i];
                if (filedAsRows)
                {
                    GameAction row = LiveOnlyIncome.BuildRows(TransactionReasons.Cheating,
                        new StrategyConversionQuery { InputFunds = incomes[i] }, ut)[0];
                    row.CaptureSequence = cseq++;
                    ledger.Add(row);
                }
                // A plain spend in between.
                ledger.Add(new GameAction
                {
                    ActionId = "buy" + i, UT = ut + 1, CaptureSequence = cseq++, Type = GameActionType.FundsSpending,
                    FundsSpent = 5f, FundsSpendingSource = FundsSpendingSource.VesselBuild,
                });
                live -= 5;
                live = PatchFunds(ledger, funds, ut + 2, live, out m);
            }
            adjustments = ledger.Count(a => a.Type == GameActionType.StockPoolAdjustment);
            return live;
        }

        // catches: a Re-Fly start bringing dropped income back. OnLoad's in-session drop runs
        // first, then RewindInvoker.ConsumePostLoad replaces the whole ledger with the bundle it
        // captured before the invoke, which still holds the income earned after the rewind point.
        [Fact]
        public void ReFlyStart_BundleRestore_DoesNotBringDroppedIncomeBack()
        {
            MilestoneStore.ResetForTesting();
            GroupHierarchyStore.ResetForTesting();
            GroupHierarchyStore.ResetGroupsForTesting();
            try
            {
                LiveOnlyIncome.Capture(TransactionReasons.Cheating, new StrategyConversionQuery { InputFunds = 100 }, 300);
                LiveOnlyIncome.Capture(TransactionReasons.Cheating, new StrategyConversionQuery { InputFunds = 900 }, 800);
                string lateId = Ledger.Actions.Single(a => a.IsLiveOnlyIncome && a.UT == 800).ActionId;
                ReconciliationBundle bundle = ReconciliationBundle.Capture();

                Assert.Equal(1, LiveOnlyIncome.DropAfterTimeTravelLoad(LoadKind.ReFlyStart, 500));
                Assert.DoesNotContain(Ledger.Actions, a => a.ActionId == lateId);
                ReconciliationBundle.Restore(bundle);
                Assert.Contains(Ledger.Actions, a => a.ActionId == lateId);   // the defect's shape

                StockPoolReference.Funds.Counted[lateId] = new CountedRow { Effect = 900, Stepped = 900 };
                Assert.Equal(1, LiveOnlyIncome.DropAfterReFlyBundleRestore(500, 510));
                Assert.DoesNotContain(Ledger.Actions, a => a.ActionId == lateId);
                Assert.Single(Ledger.Actions.Where(a => a.IsLiveOnlyIncome));
                Assert.Equal(300, Ledger.Actions.Single(a => a.IsLiveOnlyIncome).UT);
                Assert.False(StockPoolReference.Funds.Counted.ContainsKey(lateId));
                Assert.Contains(logLines, l => l.Contains("[LiveOnlyIncome]") && l.Contains("Re-Fly bundle restore")
                    && l.Contains("ut=500"));
                Assert.Contains(logLines, l => l.Contains("[INFO][LiveOnlyIncome]") && l.Contains("kind=ReFlyStart")
                    && l.Contains("dropped 1 live-only income row(s)"));
            }
            finally
            {
                MilestoneStore.ResetForTesting();
                GroupHierarchyStore.ResetForTesting();
                GroupHierarchyStore.ResetGroupsForTesting();
            }
        }

        // catches: the Re-Fly drop judging at a UT other than the in-session drop's.
        [Fact]
        public void ReFlyDropUT_IsTheLoadedSavesClock_ElseTheFallback()
        {
            Assert.Equal(500.0, LiveOnlyIncome.ResolveReFlyDropUT(500, 510));
            Assert.Equal(510.0, LiveOnlyIncome.ResolveReFlyDropUT(double.NaN, 510));
            Assert.Equal(510.0, LiveOnlyIncome.ResolveReFlyDropUT(double.PositiveInfinity, 510));
        }

        // catches: the recorder door or the time-travel load no longer reaching the rule (both
        // live-only call sites run only inside KSP, so a source gate is the unit-level witness).
        [Fact]
        public void Wiring_RecorderFilesIncome_AndTheTimeTravelLoadDropsIt()
        {
            string recorder = ReadSource("GameStateRecorder.cs");
            int door = recorder.IndexOf("void OnCurrencyModified(", StringComparison.Ordinal);
            Assert.True(door >= 0, "OnCurrencyModified not found");
            int capture = recorder.IndexOf("LiveOnlyIncome.Capture(qry.reason, snapshot, ut);", door, StringComparison.Ordinal);
            int legs = recorder.IndexOf("StrategyConversionCapture.EvaluateLegs(snapshot)", door, StringComparison.Ordinal);
            Assert.True(capture > door, "the currency-modified door no longer files live-only income");
            Assert.True(legs > capture,
                "live-only income must be filed before the strategy-leg early return (a query with no leg still pays income)");

            // Both timeline-break sites (the plain rewind's own branch and the in-session
            // branch's refined kind) drop later income right after recording their break, at
            // the same UT.
            string scenario = System.Text.RegularExpressions.Regex.Replace(ReadSource("ParsekScenario.cs"), @"\s+", " ");
            int plainNote = scenario.IndexOf("StockShadowTakeBack.NoteLoad( LoadKind.PlainRewind, rewindWorldUT,",
                StringComparison.Ordinal);
            int plainDrop = scenario.IndexOf("LiveOnlyIncome.DropAfterTimeTravelLoad(LoadKind.PlainRewind, rewindWorldUT);",
                StringComparison.Ordinal);
            int rewindHandler = scenario.IndexOf("HandleRewindOnLoad(node, recordings);", StringComparison.Ordinal);
            Assert.True(plainNote >= 0, "the plain rewind's NoteLoad site not found");
            Assert.True(plainDrop > plainNote && plainDrop < rewindHandler,
                "the plain rewind no longer drops later live-only income before HandleRewindOnLoad");

            int noteLoad = scenario.IndexOf("refinedLoadKind, loadedWorldUT, CaptureSequence.CurrentHighWater());",
                StringComparison.Ordinal);
            int drop = scenario.IndexOf("LiveOnlyIncome.DropAfterTimeTravelLoad(refinedLoadKind, loadedWorldUT);",
                StringComparison.Ordinal);
            Assert.True(noteLoad >= 0, "the in-session NoteLoad site not found");
            Assert.True(drop > noteLoad, "the in-session time-travel load no longer drops later live-only income");
            Assert.Equal(-1, scenario.IndexOf("LiveOnlyIncome.DropAfterTimeTravelLoad(", drop + 1, StringComparison.Ordinal));
            Assert.Equal(-1, scenario.IndexOf("LiveOnlyIncome.DropAfterTimeTravelLoad(", plainDrop + 1, drop - plainDrop - 1,
                StringComparison.Ordinal));

            // The Re-Fly start's bundle restore replaces the ledger after OnLoad's drop: the
            // drop runs again right after it, at the loaded quicksave's clock.
            string invoker = System.Text.RegularExpressions.Regex.Replace(
                SourceScanText.StripCSharpComments(ReadSource("RewindInvoker.cs")), @"\s+", " ");
            int consume = invoker.IndexOf("internal static void ConsumePostLoad(", StringComparison.Ordinal);
            int restore = invoker.IndexOf("ReconciliationBundle.Restore( bundle, retireCutoffUT,", consume, StringComparison.Ordinal);
            int reDrop = invoker.IndexOf("LiveOnlyIncome.DropAfterReFlyBundleRestore(loadedClockUT, retireCutoffUT);",
                StringComparison.Ordinal);
            Assert.True(consume >= 0 && restore > consume, "ConsumePostLoad's bundle restore not found");
            Assert.True(reDrop > restore, "ConsumePostLoad no longer drops live-only income after its bundle restore");
            int nextRestore = invoker.IndexOf("ReconciliationBundle.Restore(", restore + 1, StringComparison.Ordinal);
            Assert.True(nextRestore < 0 || reDrop < nextRestore,
                "the re-drop must follow the success-path restore, not a later one");
            Assert.True(invoker.IndexOf("loadedClockUT = KerbalsModule.ReadLoadedSaveUT();", consume, StringComparison.Ordinal) > consume,
                "ConsumePostLoad no longer reads the loaded quicksave's clock the drop judges at");
        }

        private static string ReadSource(string file)
        {
            string path = Path.GetFullPath(Path.Combine(
                AppDomain.CurrentDomain.BaseDirectory,
                "..", "..", "..", "..", "..", "Source", "Parsek", file));
            Assert.True(File.Exists(path), "source file not found for scan: " + path);
            return File.ReadAllText(path);
        }
    }
}
