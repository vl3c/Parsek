using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Reflection;
using KSP.UI.Screens.Editor;
using Parsek.Patches;
using Xunit;

namespace Parsek.Tests
{
    /// <summary>
    /// Todo PART-UPGRADE-PURCHASE-UNRESERVED: a part upgrade bought from the part list
    /// tooltip is captured as a ledger cost row, re-applied to stock when the clock reaches a
    /// committed one, refused while the committed timeline buys it later or while its price
    /// would dig into the click reservation, and reconciled after stock's charge.
    /// </summary>
    [Collection("Sequential")]
    public class PartUpgradePurchaseTests : IDisposable
    {
        private const string Wheel = "ReactionWheelTorqueUpgrade";
        private const string Engine = "EngineIspUpgrade";

        private readonly List<string> logLines = new List<string>();
        private readonly List<string> dialogActions = new List<string>();
        private readonly List<string> dialogReasons = new List<string>();

        private static readonly Func<double, string> Fmt =
            ut => "D" + ((long)(ut / 100.0)).ToString(CultureInfo.InvariantCulture);

        public PartUpgradePurchaseTests()
        {
            ParsekLog.ResetTestOverrides();
            ParsekLog.VerboseOverrideForTesting = true;
            ParsekLog.TestSinkForTesting = line => logLines.Add(line);
            GameStateStore.SuppressLogging = true;
            RecordingStore.SuppressLogging = true;
            GameStateStore.ResetForTesting();
            MilestoneStore.ResetForTesting();
            RecordingStore.ResetForTesting();
            LedgerOrchestrator.ResetForTesting();
            GameStateRecorder.ResetForTesting();
            EffectiveState.ResetCachesForTesting();
            CommittedFutureIndexCache.ResetForTesting();
            ParsekScenario.ResetInstanceForTesting();
            KspStatePatcher.ResetForTesting();
            KspStatePatcher.SuppressUnityCallsForTesting = true;
            ClickReservationGate.ResetForTesting();
            StockUiPartPurchase.ResetForTesting();
            GameStateRecorder.BypassEntryPurchaseAfterResearchProviderForTesting = () => false;
            StockUiPartUpgradePurchase.UnlockedInStockProviderForTesting = name => false;
            CommittedActionDialog.TestHookForTesting = (action, reason, detail) =>
            {
                dialogActions.Add(action);
                dialogReasons.Add(reason);
            };
        }

        public void Dispose()
        {
            CommittedActionDialog.TestHookForTesting = null;
            LedgerOrchestrator.DeferOneFrameForTesting = null;
            ClickReservationGate.ResetForTesting();
            StockUiPartPurchase.ResetForTesting();
            KspStatePatcher.ResetForTesting();
            GameStateStore.ResetForTesting();
            MilestoneStore.ResetForTesting();
            RecordingStore.ResetForTesting();
            LedgerOrchestrator.ResetForTesting();
            GameStateRecorder.ResetForTesting();
            EffectiveState.ResetCachesForTesting();
            CommittedFutureIndexCache.ResetForTesting();
            ParsekScenario.ResetInstanceForTesting();
            ParsekLog.ResetTestOverrides();
            ParsekLog.SuppressLogging = true;
        }

        private static GameAction UpgradeRow(double ut, string upgrade, float cost, string recordingId = null) =>
            new GameAction
            {
                UT = ut,
                Type = GameActionType.FundsSpending,
                FundsSpendingSource = FundsSpendingSource.PartUpgrade,
                FundsSpent = cost,
                DedupKey = upgrade,
                RecordingId = recordingId
            };

        private static GameAction PartRow(double ut, string part, float cost) =>
            new GameAction
            {
                UT = ut,
                Type = GameActionType.FundsSpending,
                FundsSpendingSource = FundsSpendingSource.Other,
                FundsSpent = cost,
                DedupKey = part
            };

        private static CommittedFutureIndex Index(params GameAction[] rows)
        {
            return CommittedFutureIndex.Build(rows,
                id => id == "rec-committed",
                id => id == "rec-committed" ? "Pod Test" : null,
                null);
        }

        private static PartUpgradeHandler.Upgrade Up(string name, float entryCost = 4000f) =>
            new PartUpgradeHandler.Upgrade { name = name, title = name + " title", entryCost = entryCost, techRequired = "start" };

        // ---------------- capture ----------------

        [Theory]
        [InlineData(4000f, false, 4000f)]
        [InlineData(4000f, true, 0f)]
        [InlineData(0f, false, 0f)]
        [InlineData(-5f, false, 0f)]
        public void ChargedCost_IsWhatStocksFundingTakes(float entryCost, bool bypass, float expected)
        {
            Assert.Equal(expected, GameStateRecorder.ComputePartUpgradeChargedCost(entryCost, bypass));
        }

        [Fact]
        public void Event_CarriesTheChargedCostAndThePrice_AndTheDeltaIsTheCharge()
        {
            var evt = GameStateRecorder.CreatePartUpgradePurchasedEvent(Wheel, 4000f, false, 500.0, 10000.0);
            Assert.Equal(GameStateEventType.PartUpgradePurchased, evt.eventType);
            Assert.Equal(Wheel, evt.key);
            Assert.Equal("cost=4000;entryCost=4000", evt.detail);
            Assert.Equal(-4000.0, evt.valueAfter - evt.valueBefore, 3);

            var free = GameStateRecorder.CreatePartUpgradePurchasedEvent(Wheel, 4000f, true, 500.0, 10000.0);
            Assert.Equal("cost=0;entryCost=4000", free.detail);
            Assert.Equal(0.0, free.valueAfter - free.valueBefore, 3);
        }

        [Fact]
        public void Event_TypeIsAppendOnly_AndDisplaysAsAnUpgrade()
        {
            Assert.Equal(25, (int)GameStateEventType.PartUpgradePurchased);
            Assert.Equal(8, (int)FundsSpendingSource.PartUpgrade);
            var evt = GameStateRecorder.CreatePartUpgradePurchasedEvent(Wheel, 4000f, false, 500.0, 10000.0);
            Assert.Equal("Upgrade", GameStateEventDisplay.GetDisplayCategory(evt.eventType));
            Assert.Equal("Part upgrade \"" + Wheel + "\" (4000 funds)", GameStateEventDisplay.GetDisplayDescription(evt));
        }

        [Fact]
        public void Converter_MakesAFundsSpendingCostRow_KeyedByTheUpgradeName()
        {
            var evt = GameStateRecorder.CreatePartUpgradePurchasedEvent(Wheel, 4000f, false, 500.0, 10000.0);
            evt.captureSequence = 42;
            var a = GameStateEventConverter.ConvertEvent(evt, null);

            Assert.NotNull(a);
            Assert.Equal(GameActionType.FundsSpending, a.Type);
            Assert.Equal(FundsSpendingSource.PartUpgrade, a.FundsSpendingSource);
            Assert.Equal(Wheel, a.DedupKey);
            Assert.Equal(4000f, a.FundsSpent);
            Assert.Equal(500.0, a.UT);
            Assert.Equal(42, a.CaptureSequence);
            Assert.True(ReservedFloorPlan.IsAnyPoolCostRow(a));
        }

        [Fact]
        public void ActionKey_KeepsAnUpgradeApartFromAPartOfTheSameName()
        {
            Assert.NotEqual(LedgerOrchestrator.GetActionKey(UpgradeRow(1, "same", 1f)),
                LedgerOrchestrator.GetActionKey(PartRow(1, "same", 1f)));
        }

        [Fact]
        public void Display_NamesTheUpgrade()
        {
            Assert.Equal("Part upgrade: " + Wheel + " -4000",
                TimelineEntryDisplay.GetGameActionText(UpgradeRow(500, Wheel, 4000f), null, null));
        }

        [Fact]
        public void Timeline_TheLegacyEventTwinIsDedupedAgainstTheLedgerRow()
        {
            var milestone = new Milestone
            {
                Committed = true,
                Epoch = 0,
                Events = new List<GameStateEvent>
                {
                    GameStateRecorder.CreatePartUpgradePurchasedEvent(Wheel, 4000f, false, 320, 10000.0),
                }
            };
            var row = UpgradeRow(320, Wheel, 4000f);
            row.Effective = true;
            var result = TimelineBuilder.Build(
                new List<Recording>(), new List<GameAction> { row }, new List<Milestone> { milestone }, _ => true);

            Assert.DoesNotContain(result, e => e.Source == TimelineSource.Legacy);
            Assert.Contains(result, e => e.Source == TimelineSource.GameAction
                && e.DisplayText == "Part upgrade: " + Wheel + " -4000");
        }

        // ---------------- the block predicate ----------------

        [Fact]
        public void Predicate_BlocksBeforeTheCommittedUT_LiftsAtIt()
        {
            var index = Index(UpgradeRow(500, Wheel, 4000f));
            Assert.True(StockUiReservationPredicates.IsPartUpgradePurchaseBlocked(index, Wheel, 499.9, false, false));
            Assert.False(StockUiReservationPredicates.IsPartUpgradePurchaseBlocked(index, Wheel, 500, false, false));
            Assert.False(StockUiReservationPredicates.IsPartUpgradePurchaseBlocked(index, Engine, 100, false, false));
        }

        [Fact]
        public void Predicate_BypassOrAlreadyUnlocked_NeverBlocks()
        {
            var index = Index(UpgradeRow(500, Wheel, 4000f));
            Assert.False(StockUiReservationPredicates.IsPartUpgradePurchaseBlocked(index, Wheel, 100, true, false));
            Assert.False(StockUiReservationPredicates.IsPartUpgradePurchaseBlocked(index, Wheel, 100, false, true));
        }

        [Fact]
        public void Predicate_PartAndUpgradeRowsDoNotCrossBlock()
        {
            var index = Index(UpgradeRow(500, "same", 4000f), PartRow(600, "other", 100f));
            Assert.Null(StockUiReservationPredicates.CommittedPartPurchaseAfter(index, "same", 100));
            Assert.Null(StockUiReservationPredicates.CommittedPartUpgradePurchaseAfter(index, "other", 100));
            Assert.NotNull(StockUiReservationPredicates.CommittedPartUpgradePurchaseAfter(index, "same", 100));
            Assert.Contains("upgrade=1", index.DescribeCounts());
            Assert.Contains("part=1", index.DescribeCounts());
        }

        [Fact]
        public void Predicate_UncommittedRecordingsRow_IsIgnored()
        {
            var index = Index(UpgradeRow(500, Wheel, 4000f, "rec-other"), UpgradeRow(500, Engine, 4000f, "rec-committed"));
            Assert.False(StockUiReservationPredicates.IsPartUpgradePurchaseBlocked(index, Wheel, 100, false, false));
            Assert.True(StockUiReservationPredicates.IsPartUpgradePurchaseBlocked(index, Engine, 100, false, false));
        }

        [Fact]
        public void Decide_ExplainsWithTheCommittedDate()
        {
            var d = StockUiPartUpgradePurchase.Decide(Index(UpgradeRow(500, Wheel, 4000f)), Wheel, 100, false, false, Fmt);
            Assert.True(d.Blocked);
            Assert.Equal(500.0, d.UT);
            Assert.Equal(ReservationExplanation.PartUpgradePurchase(
                StockUiReservationPredicates.CommittedPartUpgradePurchaseAfter(Index(UpgradeRow(500, Wheel, 4000f)), Wheel, 100),
                Fmt).Body, d.Why);
            Assert.False(StockUiPartUpgradePurchase.Decide(Index(UpgradeRow(500, Wheel, 4000f)), Wheel, 600, false, false, Fmt).Blocked);
        }

        [Fact]
        public void TimelineHold_NamesTheUpgradeControl_OnlyWhileItIsBlocked()
        {
            var row = UpgradeRow(500, Wheel, 4000f);
            var index = Index(row);
            var entry = new TimelineEntry { UT = 500, Action = row };
            Assert.Equal("Holds Purchase for this part upgrade until D5.",
                ReservationExplanation.ForTimelineRow(entry, index, 100, Fmt, null, null, n => n == Wheel));
            Assert.Null(ReservationExplanation.ForTimelineRow(entry, index, 100, Fmt, null, null, n => false));
        }

        [Theory]
        [InlineData(false, false, 4000f, true)]
        [InlineData(true, false, 4000f, false)]
        [InlineData(false, true, 4000f, false)]
        [InlineData(false, false, 0f, false)]
        public void ReserveCheck_OnlyForAPaidLockedUpgrade(bool bypass, bool unlocked, float charged, bool expected)
        {
            Assert.Equal(expected, StockUiPartUpgradePurchase.NeedsReserveCheck(bypass, unlocked, charged));
        }

        [Fact]
        public void ReserveLayer_BlocksWithTheSharedReason_ACommittedBlockKeepsItsOwn()
        {
            var refused = new PurchaseRefusal { Refused = true, Pool = StockPool.Funds, Cost = 4000, Spendable = 100 };
            var d = StockUiPartUpgradePurchase.LayerReserve(
                new StockUiPartUpgradePurchase.Decision { UpgradeName = Wheel, UT = double.NaN }, refused);
            Assert.True(d.Blocked);
            Assert.Equal("Reserved for later costs and contract penalties, blocked by timeline. Needs 4,000 funds, 100 free.", d.Why);
            var committed = new StockUiPartUpgradePurchase.Decision { UpgradeName = Wheel, Blocked = true, Why = "c", UT = 5 };
            Assert.Equal("c", StockUiPartUpgradePurchase.LayerReserve(committed, refused).Why);
            Assert.False(StockUiPartUpgradePurchase.LayerReserve(
                new StockUiPartUpgradePurchase.Decision { UpgradeName = Wheel }, default(PurchaseRefusal)).Blocked);
        }

        // ---------------- pairing: greyed set == refused set ----------------

        private static bool InvokeBackstop(PartUpgradeHandler.Upgrade up)
        {
            var prefix = typeof(PartListTooltipPurchaseUpgradePatch).GetMethod("Prefix", BindingFlags.Static | BindingFlags.NonPublic);
            Assert.NotNull(prefix);
            return (bool)prefix.Invoke(null, new object[] { up });
        }

        [Fact]
        public void Pairing_TheGreyedUpgradesAreExactlyTheRefusedOnes_WithTheSameText()
        {
            Ledger.AddAction(UpgradeRow(500, Wheel, 4000f));
            CommittedFutureIndexCache.NowUtProviderForTesting = () => 100;
            var ups = new[] { Up(Wheel), Up(Engine) };

            var greyed = ups.Where(u => StockUiPartUpgradePurchase.DecideLive(u).Blocked).Select(u => u.name).ToArray();
            var refused = ups.Where(u => !InvokeBackstop(u)).Select(u => u.name).ToArray();

            Assert.Equal(new[] { Wheel }, greyed);
            Assert.Equal(greyed, refused);
            Assert.Equal(new[] { StockUiPartUpgradePurchase.DecideLive(ups[0]).Why }, dialogReasons.ToArray());
            Assert.Equal(new[] { "Cannot purchase \"" + Wheel + " title\"" }, dialogActions.ToArray());
            Assert.Contains(logLines, l => l.Contains("[PartPurchasePatch]")
                && l.Contains("Blocking part upgrade purchase: '" + Wheel + "'")
                && l.Contains("source=upgrade tooltip purchase")
                && l.Contains("committed future purchase ut=500"));
        }

        [Fact]
        public void Pairing_UnlockedOrBypass_NothingRefused()
        {
            Ledger.AddAction(UpgradeRow(500, Wheel, 4000f));
            CommittedFutureIndexCache.NowUtProviderForTesting = () => 100;
            StockUiPartUpgradePurchase.UnlockedInStockProviderForTesting = n => true;
            Assert.True(InvokeBackstop(Up(Wheel)));
            StockUiPartUpgradePurchase.UnlockedInStockProviderForTesting = n => false;
            GameStateRecorder.BypassEntryPurchaseAfterResearchProviderForTesting = () => true;
            Assert.True(InvokeBackstop(Up(Wheel)));
            Assert.Empty(dialogReasons);
        }

        [Fact]
        public void Backstop_BypassedDuringActionReplay_AndANullUpgradePassesThrough()
        {
            Ledger.AddAction(UpgradeRow(500, Wheel, 4000f));
            CommittedFutureIndexCache.NowUtProviderForTesting = () => 100;
            using (SuppressionGuard.ResourcesAndReplay())
            {
                Assert.True(InvokeBackstop(Up(Wheel)));
            }
            Assert.True(InvokeBackstop(null));
            Assert.Empty(dialogReasons);
            Assert.Contains(logLines, l => l.Contains("[PartPurchasePatch]") && l.Contains("action replay in progress"));
        }

        // ---------------- state patch ----------------

        [Fact]
        public void StatePatch_Names_UpgradeRowsAtOrBeforeTheCutoff_Only_SortedAndDeduped()
        {
            var actions = new List<GameAction>
            {
                UpgradeRow(300, Wheel, 4000f),
                UpgradeRow(100, Engine, 0f),
                UpgradeRow(200, Engine, 10f),
                UpgradeRow(301, "late", 1f),
                UpgradeRow(50, "", 1f),
                PartRow(10, "mk1pod.v2", 600f),
            };
            Assert.Equal(new[] { Engine, Wheel },
                KspStatePatcher.BuildUnlockedUpgradeNamesForPatch(actions, 300).ToArray());
            Assert.Equal(new[] { Engine },
                KspStatePatcher.BuildUnlockedUpgradeNamesForPatch(actions, 299.9).ToArray());
            Assert.DoesNotContain("mk1pod.v2", KspStatePatcher.BuildPurchasedPartNamesForPatch(
                new List<GameAction> { UpgradeRow(10, "mk1pod.v2", 1f) }, 300));
        }

        [Fact]
        public void StatePatch_Plan_IsClampedToTheReadyLiveClock()
        {
            var actions = new List<GameAction> { UpgradeRow(300, Wheel, 4000f), UpgradeRow(100, Engine, 4000f) };
            double? cutoff;
            Assert.Equal(new[] { Engine },
                KspStatePatcher.PlanUnlockedUpgradeNamesForPatch(actions, null, null, 200.0, out cutoff).ToArray());
            Assert.Equal(200.0, cutoff);
            Assert.Null(KspStatePatcher.PlanUnlockedUpgradeNamesForPatch(actions, null, null, 0.0, out cutoff));
        }

        [Fact]
        public void StatePatch_AddsOnly_IsIdempotent_AndSkipsUnresearchedAndUnknownUpgrades()
        {
            var known = new HashSet<string> { Wheel, Engine, "locked" };
            var researched = new HashSet<string> { Wheel, Engine };
            var unlocked = new HashSet<string> { Engine, "someOther" };
            int writes = 0;
            Func<KspStatePatcher.UpgradeUnlockPatchResult> apply = () => KspStatePatcher.ApplyUpgradeUnlockPatch(
                new[] { Wheel, Engine, "locked", "notDefined" },
                known.Contains, researched.Contains, unlocked.Contains,
                n => { unlocked.Add(n); writes++; });

            var first = apply();
            Assert.Equal(1, first.Added);
            Assert.Equal(new[] { Wheel }, first.AddedNames.ToArray());
            Assert.Equal(1, first.AlreadyUnlocked);
            Assert.Equal(1, first.TechNotResearched);
            Assert.Equal(1, first.UnknownUpgrade);
            Assert.Contains("someOther", unlocked);
            Assert.DoesNotContain("locked", unlocked);

            var second = apply();
            Assert.Equal(0, second.Added);
            Assert.Equal(2, second.AlreadyUnlocked);
            Assert.Equal(1, writes);
        }

        [Fact]
        public void StatePatch_SkipsWithUnityCallsSuppressed_AndLogs()
        {
            KspStatePatcher.UpgradeUnlockLiveUtProviderForTesting = () => 1000.0;
            KspStatePatcher.PatchUnlockedUpgrades(new List<GameAction> { UpgradeRow(300, Wheel, 4000f) }, null, null);
            Assert.Contains(logLines, l => l.Contains("PatchUnlockedUpgrades: Unity calls suppressed for testing"));
        }

        // ---------------- the Space Center door: reconcile after stock's charge ----------------

        [Fact]
        public void DeferPredicate_HireAndRnDChargedRows_OnlyWithAHost()
        {
            Assert.True(LedgerOrchestrator.ShouldDeferKscReconcileOneFrame(UpgradeRow(1, Wheel, 1f), true));
            Assert.True(LedgerOrchestrator.ShouldDeferKscReconcileOneFrame(PartRow(1, "p", 1f), true));
            Assert.True(LedgerOrchestrator.ShouldDeferKscReconcileOneFrame(
                new GameAction { Type = GameActionType.KerbalHire }, true));
            Assert.False(LedgerOrchestrator.ShouldDeferKscReconcileOneFrame(UpgradeRow(1, Wheel, 1f), false));
            Assert.False(LedgerOrchestrator.ShouldDeferKscReconcileOneFrame((GameAction)null, true));
            foreach (var source in new[] { FundsSpendingSource.VesselBuild, FundsSpendingSource.Strategy,
                         FundsSpendingSource.StrategyConverter, FundsSpendingSource.FacilityUpgrade })
            {
                var row = UpgradeRow(1, Wheel, 1f);
                row.FundsSpendingSource = source;
                Assert.False(LedgerOrchestrator.ShouldDeferKscReconcileOneFrame(row, true), source.ToString());
            }
            Assert.False(LedgerOrchestrator.ShouldDeferKscReconcileOneFrame(
                new GameAction { Type = GameActionType.FacilityUpgrade }, true));
        }

        [Fact]
        public void OnKscSpending_Upgrade_WritesTheRow_AndReconcilesAfterStocksCharge()
        {
            ParsekLog.SuppressLogging = false;
            Action deferred = null;
            LedgerOrchestrator.DeferOneFrameForTesting = a => deferred = a;

            LedgerOrchestrator.OnKscSpending(
                GameStateRecorder.CreatePartUpgradePurchasedEvent(Wheel, 4000f, false, 600.0, 10000.0));

            Assert.NotNull(deferred);
            var row = Ledger.Actions.Single(a => a.Type == GameActionType.FundsSpending);
            Assert.Equal(FundsSpendingSource.PartUpgrade, row.FundsSpendingSource);
            Assert.Equal(4000f, row.FundsSpent);
            Assert.Contains(logLines, l => l.Contains("[LedgerOrchestrator]")
                && l.Contains("OnKscSpending: deferring the reconcile and recalc one frame for FundsSpending"));
            Assert.DoesNotContain(logLines, l => l.Contains("KSC reconciliation (funds)"));

            // Stock's Funding charges it after the handler returns, as RnDPartPurchase.
            var charge = new GameStateEvent
            {
                ut = 600.0,
                eventType = GameStateEventType.FundsChanged,
                key = "RnDPartPurchase",
                valueBefore = 14000.0,
                valueAfter = 10000.0
            };
            GameStateStore.AddEvent(ref charge);

            deferred();

            Assert.Contains(logLines, l => l.Contains("[RecalcEngine]") && l.Contains("Recalculate complete"));
            Assert.DoesNotContain(logLines, l => l.Contains("KSC reconciliation (funds)"));
        }
    }
}
