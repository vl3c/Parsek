using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Reflection;
using KSP.UI.Screens;
using Parsek.Patches;
using Xunit;
using Contract = Contracts.Contract;

namespace Parsek.Tests
{
    /// <summary>
    /// Stock-UI overlays PR 4: the contract-slot block (C2,
    /// docs/dev/research/stock-ui-reservation-overlays-2026-09-25.md section 10 step 4).
    /// Accepting a contract the committed timeline does not accept is refused when it would
    /// leave no Mission Control slot for a committed accept later. The peak model is
    /// <see cref="ContractSlotReservation.Forecast"/>; the greyed Accept, the Contract.Accept
    /// backstop and Contract Configurator's CanAccept all read one decision.
    /// </summary>
    [Collection("Sequential")]
    public class ContractSlotReservationTests : IDisposable
    {
        private readonly List<string> logLines = new List<string>();
        private int dialogCount;
        private string dialogAction;
        private string dialogReason;

        private static readonly Func<double, string> Fmt =
            ut => "D" + ((long)(ut / 100.0)).ToString(CultureInfo.InvariantCulture);

        private const string Rule =
            "Parsek's timeline is fixed once committed, so this cannot happen earlier or twice.";

        private const string WayOut = "A slot frees when one of your active contracts ends.";

        public ContractSlotReservationTests()
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
            MissionControlStockUi.ResetForTesting();
            ContractConfiguratorCompat.ResetForTesting();
            ContractSlotReservation.ResetForTesting();
            CommittedFutureIndexCache.NowUtProviderForTesting = () => 100.0;
            CommittedActionDialog.TestHookForTesting = (action, reason, detail) =>
            {
                dialogCount++;
                dialogAction = action;
                dialogReason = reason;
            };
        }

        public void Dispose()
        {
            CommittedActionDialog.TestHookForTesting = null;
            GameStateStore.ResetForTesting();
            MilestoneStore.ResetForTesting();
            RecordingStore.ResetForTesting();
            LedgerOrchestrator.ResetForTesting();
            GameStateRecorder.ResetForTesting();
            EffectiveState.ResetCachesForTesting();
            CommittedFutureIndexCache.ResetForTesting();
            MissionControlStockUi.ResetForTesting();
            ContractConfiguratorCompat.ResetForTesting();
            ContractSlotReservation.ResetForTesting();
            ParsekLog.ResetTestOverrides();
        }

        // ---------------------------------------------------------------- fixtures

        private sealed class FakeContract : Contract
        {
            private readonly string title;

            internal FakeContract(string title, Guid guid, State state)
            {
                this.title = title;
                typeof(Contract).GetField("contractGuid", BindingFlags.Instance | BindingFlags.NonPublic).SetValue(this, guid);
                typeof(Contract).GetField("state", BindingFlags.Instance | BindingFlags.NonPublic).SetValue(this, state);
            }

            protected override string GetTitle() => title;
        }

        private static GameAction Accept(double ut, string id, string rec = null, string title = null,
            double deadlineUT = double.NaN) =>
            new GameAction
            {
                UT = ut, Type = GameActionType.ContractAccept, ContractId = id, RecordingId = rec,
                ContractTitle = title, DeadlineUT = deadlineUT
            };

        private static GameAction Resolve(GameActionType type, double ut, string id) =>
            new GameAction { UT = ut, Type = type, ContractId = id };

        private static GameAction Complete(double ut, string id) => Resolve(GameActionType.ContractComplete, ut, id);
        private static GameAction Fail(double ut, string id) => Resolve(GameActionType.ContractFail, ut, id);
        private static GameAction Cancel(double ut, string id) => Resolve(GameActionType.ContractCancel, ut, id);

        private static GameAction Upgrade(double ut, string facilityId, int toLevel) =>
            new GameAction { UT = ut, Type = GameActionType.FacilityUpgrade, FacilityId = facilityId, ToLevel = toLevel };

        private static CommittedFutureIndex Index(params GameAction[] actions) =>
            CommittedFutureIndex.Build(actions, id => id == "rec", id => id == "rec" ? "Mun Lander 3" : null, null);

        private static ContractSlotForecast Forecast(CommittedFutureIndex index, int limit, params string[] active) =>
            ContractSlotReservation.Forecast(index, active, limit, 100.0);

        private static ContractSlotForecast Forecast(CommittedFutureIndex index, int limit, params ContractSlotHolder[] active) =>
            ContractSlotReservation.Forecast(index, active, limit, 100.0);

        // ---------------------------------------------------------------- the peak model

        [Fact]
        public void NoCommittedAccept_FreeIsLimitMinusActive_NothingReserved()
        {
            var f = Forecast(Index(), 7, "a", "b");

            Assert.Equal(7, f.LimitNow);
            Assert.Equal(2, f.ActiveNow);
            Assert.Equal(2, f.PeakCommitted);
            Assert.Equal(0, f.ReservedForLater);
            Assert.Equal(5, f.FreeSlotsForNewAcceptNow);
            Assert.Null(f.FirstStarvedAccept);
            Assert.False(f.BlocksNewAcceptNow);
        }

        [Fact]
        public void ActiveWithoutResolution_CountsThroughout_ACommittedAcceptTakesTheLastSlot()
        {
            var f = Forecast(Index(Accept(500, "c", "rec", "Rescue Bill")), 2, "a");

            Assert.Equal(1, f.ActiveNow);
            Assert.Equal(2, f.PeakCommitted);
            Assert.Equal(1, f.ReservedForLater);
            Assert.Equal(0, f.FreeSlotsForNewAcceptNow);
            Assert.True(f.BlocksNewAcceptNow);
            Assert.Equal("c", f.FirstStarvedAccept.Key);
            Assert.Equal(500, f.FirstStarvedAccept.UT);
        }

        [Theory]
        [InlineData(GameActionType.ContractComplete)]
        [InlineData(GameActionType.ContractFail)]
        [InlineData(GameActionType.ContractCancel)]
        public void ActiveResolvedBeforeTheCommittedAccept_FreesItsSlot(GameActionType resolution)
        {
            var f = Forecast(Index(Resolve(resolution, 300, "a"), Accept(500, "c")), 2, "a");

            Assert.Equal(1, f.PeakCommitted);
            Assert.Equal(1, f.FreeSlotsForNewAcceptNow);
            Assert.False(f.BlocksNewAcceptNow);
        }

        [Fact]
        public void ActiveResolvedAfterTheCommittedAccept_StillHoldsItsSlotThere()
        {
            var f = Forecast(Index(Complete(600, "a"), Accept(500, "c")), 2, "a");

            Assert.Equal(0, f.FreeSlotsForNewAcceptNow);
            Assert.True(f.BlocksNewAcceptNow);
        }

        [Fact]
        public void UtTie_TheRemovalComesFirst()
        {
            var f = Forecast(Index(Complete(500, "a"), Accept(500, "c")), 2, "a");

            Assert.Equal(1, f.PeakCommitted);
            Assert.Equal(1, f.FreeSlotsForNewAcceptNow);
            Assert.False(f.BlocksNewAcceptNow);
        }

        [Fact]
        public void AContractAcceptedAndResolvedAtOneUt_StillHoldsItsSlotAtTheAccept()
        {
            var f = Forecast(Index(Accept(500, "c"), Complete(500, "c")), 2, "a");

            Assert.Equal(2, f.PeakCommitted);
            Assert.Equal(0, f.FreeSlotsForNewAcceptNow);
            Assert.True(f.BlocksNewAcceptNow);
        }

        [Fact]
        public void CommittedAcceptsAddAndTheirResolutionsRemove_ThePeakIsTheBusiestMoment()
        {
            // limit 3, active a. c accepted 200, done 300; d accepted 400; e accepted 450.
            var index = Index(Accept(200, "c"), Complete(300, "c"), Accept(400, "d"), Accept(450, "e"));

            var f = Forecast(index, 3, "a");

            Assert.Equal(3, f.PeakCommitted);
            Assert.Equal(2, f.ReservedForLater);
            Assert.Equal(0, f.FreeSlotsForNewAcceptNow);
            Assert.Equal("e", f.FirstStarvedAccept.Key);
            Assert.True(f.BlocksNewAcceptNow);
            var holders = new[] { new ContractSlotHolder("a", 10, double.NaN) };
            Assert.Equal(1, ContractSlotReservation.FreeSlotsForNewAcceptNow(index, holders, 4, 100.0));
            Assert.Equal(2, ContractSlotReservation.ReservedForLater(index, holders, 4, 100.0));
        }

        [Fact]
        public void EarliestStarvedAccept_IsNamed()
        {
            // limit 2: c at 200 already fills it; d at 400 after c is done fills it again.
            var f = Forecast(Index(Accept(200, "c"), Complete(300, "c"), Accept(400, "d")), 2, "a");

            Assert.Equal("c", f.FirstStarvedAccept.Key);
            Assert.Equal(200, f.FirstStarvedAccept.UT);
        }

        [Fact]
        public void ACommittedMissionControlUpgrade_RaisesTheLimitFromItsUt()
        {
            string mc = ContractSlotReservation.MissionControlFacilityId;
            var before = Forecast(Index(Upgrade(300, mc, 2), Accept(400, "c"), Accept(450, "d")), 2, "a");
            var tie = Forecast(Index(Upgrade(400, mc, 2), Accept(400, "c")), 2, "a");
            var after = Forecast(Index(Accept(400, "c"), Upgrade(600, mc, 2)), 2, "a");
            var otherFacility = Forecast(Index(Upgrade(300, "SpaceCenter/Administration", 2), Accept(400, "c")), 2, "a");

            // Level 2 = 7 slots: 7 - 3 = 4 at 450, but now is still 2 - 1 = 1.
            Assert.Equal(1, before.FreeSlotsForNewAcceptNow);
            Assert.False(before.BlocksNewAcceptNow);
            Assert.False(tie.BlocksNewAcceptNow);
            Assert.True(after.BlocksNewAcceptNow);
            Assert.True(otherFacility.BlocksNewAcceptNow);
        }

        [Fact]
        public void TheUpgradeLimit_FollowsTheInjectedStockRule()
        {
            var f = ContractSlotReservation.Forecast(
                Index(Upgrade(300, ContractSlotReservation.MissionControlFacilityId, 3), Accept(400, "c"), Accept(410, "d")),
                new[] { "a" }, 2, 100.0, level => level == 3 ? int.MaxValue : 2);

            Assert.False(f.BlocksNewAcceptNow);
            Assert.Equal(1, f.FreeSlotsForNewAcceptNow);
        }

        [Fact]
        public void StockLevelThreeLimit_IntMaxValue_DoesNotOverflow()
        {
            var f = Forecast(Index(Accept(500, "c")), int.MaxValue, "a");

            Assert.Equal(int.MaxValue - 2, f.FreeSlotsForNewAcceptNow);
            Assert.False(f.BlocksNewAcceptNow);
        }

        [Fact]
        public void UncommittedRecordings_ReserveNothing()
        {
            // "live" is not in the committed set: a live or Re-Fly tree reserves no slot.
            var f = Forecast(Index(Accept(500, "c", "live")), 2, "a");

            Assert.Equal(1, f.PeakCommitted);
            Assert.False(f.BlocksNewAcceptNow);
        }

        [Fact]
        public void UtBoundary_AnAcceptAtNowIsAlreadyApplied_OneJustAheadCounts()
        {
            var atNow = Forecast(Index(Accept(100.0, "c")), 2, "a");
            var ahead = Forecast(Index(Accept(100.001, "c")), 2, "a");

            Assert.False(atNow.BlocksNewAcceptNow);
            Assert.Equal(1, atNow.FreeSlotsForNewAcceptNow);
            Assert.True(ahead.BlocksNewAcceptNow);
        }

        [Fact]
        public void AnActiveContract_IsNotCountedTwiceByAStrayFutureAccept()
        {
            var f = Forecast(Index(Accept(500, "a")), 2, "a");

            Assert.Equal(1, f.PeakCommitted);
            Assert.False(f.BlocksNewAcceptNow);
        }

        [Fact]
        public void NoSlotFreeNow_IsStocksOwnRule_NotReportedAgain()
        {
            var f = Forecast(Index(Accept(500, "c")), 2, "a", "b");

            Assert.Equal(-1, f.FreeSlotsForNewAcceptNow);
            Assert.False(f.BlocksNewAcceptNow);
        }

        [Fact]
        public void NullIndex_NoReservation()
        {
            var f = ContractSlotReservation.Forecast(null, new[] { "a", "", null, "a" }, 2, 100.0);

            Assert.Equal(1, f.ActiveNow);
            Assert.Equal(1, f.FreeSlotsForNewAcceptNow);
            Assert.False(f.BlocksNewAcceptNow);
        }

        [Fact]
        public void NormalizedMissionControlLevel_InvertsTheConverter()
        {
            Assert.Equal(0f, ContractSlotReservation.NormalizedMissionControlLevel(1));
            Assert.Equal(0.5f, ContractSlotReservation.NormalizedMissionControlLevel(2));
            Assert.Equal(1f, ContractSlotReservation.NormalizedMissionControlLevel(3));
        }

        [Fact]
        public void Describe_IsInvariant()
        {
            var prior = CultureInfo.CurrentCulture;
            try
            {
                CultureInfo.CurrentCulture = new CultureInfo("de-DE");
                var f = Forecast(Index(Accept(500.25, "c")), 2, "a");
                Assert.Equal("limitNow=2 activeNow=1 peak=2 reservedForLater=1 free=0 blocksNewAccept=true starvedAccept=c@500",
                    f.Describe());
            }
            finally
            {
                CultureInfo.CurrentCulture = prior;
            }
        }


        // ---------------------------------------------------------------- deadlines

        [Fact]
        public void ActiveDeadlineBeforeTheCommittedAccept_FreesItsSlot()
        {
            var f = Forecast(Index(Accept(500, "c")), 2, new ContractSlotHolder("a", 10, 300));

            Assert.Equal(1, f.PeakCommitted);
            Assert.Equal(1, f.FreeSlotsForNewAcceptNow);
            Assert.False(f.BlocksNewAcceptNow);
        }

        [Fact]
        public void ActiveDeadlineAtTheCommittedAcceptUt_RemovalComesFirst()
        {
            var f = Forecast(Index(Accept(500, "c")), 2, new ContractSlotHolder("a", 10, 500));

            Assert.False(f.BlocksNewAcceptNow);
        }

        [Fact]
        public void ActiveDeadlineAfterTheCommittedAccept_StillHoldsItsSlotThere()
        {
            var f = Forecast(Index(Accept(500, "c")), 2, new ContractSlotHolder("a", 10, 600));

            Assert.True(f.BlocksNewAcceptNow);
            Assert.Equal("c", f.FirstStarvedAccept.Key);
        }

        [Theory]
        [InlineData(10.0)]   // deadline == accept
        [InlineData(5.0)]    // deadline before accept (a duration captured as a UT)
        public void ActiveWithAnImplausibleDeadline_IsOpenEnded(double deadlineUT)
        {
            Assert.True(ContractsModule.IsImplausibleContractDeadline(deadlineUT, 10));

            var f = Forecast(Index(Accept(500, "c")), 2, new ContractSlotHolder("a", 10, deadlineUT));

            Assert.True(f.BlocksNewAcceptNow);
        }

        [Fact]
        public void CommittedAcceptWithADeadline_ReleasesItsSlotThere()
        {
            // limit 3, active a. c accepted at 200 with its deadline at 300; d accepted at 400.
            var withDeadline = Forecast(Index(Accept(200, "c", deadlineUT: 300), Accept(400, "d")), 3, "a");
            var openEnded = Forecast(Index(Accept(200, "c"), Accept(400, "d")), 3, "a");
            var implausible = Forecast(Index(Accept(200, "c", deadlineUT: 200), Accept(400, "d")), 3, "a");
            var atTheNextAccept = Forecast(Index(Accept(200, "c", deadlineUT: 400), Accept(400, "d")), 3, "a");

            Assert.Equal(1, withDeadline.FreeSlotsForNewAcceptNow);
            Assert.False(withDeadline.BlocksNewAcceptNow);
            Assert.True(openEnded.BlocksNewAcceptNow);
            Assert.Equal("d", openEnded.FirstStarvedAccept.Key);
            Assert.True(implausible.BlocksNewAcceptNow);
            Assert.False(atTheNextAccept.BlocksNewAcceptNow);
        }

        [Fact]
        public void TheNewContractsDeadline_BeforeTheStarvedAccept_IsNotRefused()
        {
            var f = Forecast(Index(Accept(500, "c")), 2, "a");

            Assert.True(f.BlocksNewAcceptNow);
            Assert.False(f.BlocksNewAccept(400));
            Assert.False(f.BlocksNewAccept(500));   // released at the accept's UT: removal first
            Assert.True(f.BlocksNewAccept(501));
            Assert.True(f.BlocksNewAccept(double.PositiveInfinity));
        }

        [Fact]
        public void NewAcceptReleaseUT_FollowsStocksAcceptDeadlineRule()
        {
            // now = 100
            Assert.Equal(400, ContractSlotReservation.NewAcceptReleaseUT(Contract.DeadlineType.Floating, 0, 300, 100));
            Assert.Equal(600, ContractSlotReservation.NewAcceptReleaseUT(Contract.DeadlineType.Fixed, 600, 300, 100));
            Assert.Equal(double.PositiveInfinity,
                ContractSlotReservation.NewAcceptReleaseUT(Contract.DeadlineType.None, 600, 300, 100));
            // Implausible: no duration, or a fixed deadline not after now -> open-ended.
            Assert.Equal(double.PositiveInfinity,
                ContractSlotReservation.NewAcceptReleaseUT(Contract.DeadlineType.Floating, 0, 0, 100));
            Assert.Equal(double.PositiveInfinity,
                ContractSlotReservation.NewAcceptReleaseUT(Contract.DeadlineType.Fixed, 50, 0, 100));
            Assert.Equal(double.PositiveInfinity, ContractSlotReservation.SlotReleaseUT(100, double.NaN));
        }

        [Fact]
        public void NewAcceptReleaseUT_ReadsTheLiveContractsDeadlineType()
        {
            var floating = new FakeContract("Floating", Guid.NewGuid(), Contract.State.Offered);
            floating.TimeDeadline = 300;
            var none = new FakeContract("None", Guid.NewGuid(), Contract.State.Offered);
            none.TimeDeadline = 300;
            typeof(Contract).GetField("deadlineType", BindingFlags.Instance | BindingFlags.NonPublic)
                .SetValue(none, Contract.DeadlineType.None);

            Assert.Equal(400, ContractSlotReservation.NewAcceptReleaseUT(floating, 100));
            Assert.Equal(double.PositiveInfinity, ContractSlotReservation.NewAcceptReleaseUT(none, 100));
        }

        [Fact]
        public void Decide_AndBackstop_LetThroughAContractWhoseDeadlineComesFirst()
        {
            Ledger.AddAction(Accept(500, "c-500"));
            ContractSlotReservation.ForecastProviderForTesting =
                (idx, now) => ContractSlotReservation.Forecast(idx, new[] { "a" }, 2, now);
            var index = CommittedFutureIndexCache.Current;
            var slots = ContractSlotReservation.ForecastNow(index, 100.0);

            var shortLived = MissionControlStockAnnotation.Decide(index, 100.0, "short", Contract.State.Offered,
                Fmt, slots, false, 400);
            var longLived = MissionControlStockAnnotation.Decide(index, 100.0, "long", Contract.State.Offered,
                Fmt, slots, false, 600);

            Assert.False(shortLived.Blocked);
            Assert.True(MissionControlStockAnnotation.BlocksAcceptForSlot(longLived));
            Assert.True(ContractAcceptPatch.ShouldAllowAccept("short", "Short", Contract.State.Offered, false, true, 400));
            Assert.False(ContractAcceptPatch.ShouldAllowAccept("long", "Long", Contract.State.Offered, false, true, 600));
            Assert.Equal(1, dialogCount);
        }

        // ---------------------------------------------------------------- auto-accept rows

        [Fact]
        public void CommittedAutoAcceptRows_DoNotTakeASlot()
        {
            var index = CommittedFutureIndex.Build(new[] { Accept(500, "c") }, null, null, null, id => id == "c");
            var f = Forecast(index, 2, "a");

            Assert.True(index.FirstFuture(CommittedFutureKind.ContractAccept, "c", 100).AutoAccept);
            Assert.Equal(1, f.PeakCommitted);
            Assert.False(f.BlocksNewAcceptNow);
        }

        [Fact]
        public void AutoAcceptFlag_ComesFromTheAcceptSnapshot()
        {
            var auto = new ConfigNode("CONTRACT");
            auto.AddValue("autoAccept", "True");
            var manual = new ConfigNode("CONTRACT");
            manual.AddValue("autoAccept", "False");
            GameStateStore.AddContractSnapshot("c-auto", auto, 50);
            GameStateStore.AddContractSnapshot("c-manual", manual, 50);
            Ledger.AddAction(Accept(500, "c-auto"));
            Ledger.AddAction(Accept(500, "c-manual"));

            var index = CommittedFutureIndexCache.Current;

            Assert.True(CommittedFutureIndexCache.IsAutoAcceptSnapshotNode(auto));
            Assert.False(CommittedFutureIndexCache.IsAutoAcceptSnapshotNode(manual));
            Assert.False(CommittedFutureIndexCache.IsAutoAcceptSnapshotNode(new ConfigNode("CONTRACT")));
            Assert.False(CommittedFutureIndexCache.IsAutoAcceptSnapshotNode(null));
            Assert.True(index.FirstFuture(CommittedFutureKind.ContractAccept, "c-auto", 100).AutoAccept);
            Assert.False(index.FirstFuture(CommittedFutureKind.ContractAccept, "c-manual", 100).AutoAccept);
            Assert.Equal(1, Forecast(index, 3, "a").ReservedForLater);
        }

        // ---------------------------------------------------------------- the Accept decision

        [Fact]
        public void Decide_ANonCommittedOffer_IsSlotBlocked_NotMarked()
        {
            var index = Index(Accept(500, "c", "rec", "Rescue Bill"));
            var slots = Forecast(index, 2, "a");

            var d = MissionControlStockAnnotation.Decide(index, 100, "free", Contract.State.Offered, Fmt, slots);

            Assert.Equal(StockUiDecorationKind.ContractSlot, d.Kind);
            Assert.False(d.Marked);
            Assert.True(d.Blocked);
            Assert.True(MissionControlStockAnnotation.BlocksAccept(d));
            Assert.True(MissionControlStockAnnotation.BlocksAcceptForSlot(d));
            Assert.False(MissionControlStockAnnotation.BlocksAcceptAndDecline(d));
            Assert.False(MissionControlStockAnnotation.BlocksCancel(d));
            Assert.Equal(500, d.UT);
            Assert.Equal("Slot needed on D5", d.Title);
            Assert.Equal("The committed flight 'Mun Lander 3' accepts the contract 'Rescue Bill' on D5 and needs this slot. "
                + Rule + " " + WayOut, d.Why);
            // No per-row mark: the row keeps stock's own label.
            Assert.Equal(MissionControlStockAnnotation.StockDefaultLabel("Free"),
                MissionControlStockAnnotation.ComposeRowLabel("", "Free", d));
            Assert.Equal("stock\n\n<b><color=#8fd3ff>Accept is unavailable</color></b>\n" + d.Why,
                MissionControlStockAnnotation.ComposeDetailText("stock", d));
        }

        [Fact]
        public void Decide_TheCommittedAcceptItself_StaysTheAcceptBlock_NotReportedTwice()
        {
            var index = Index(Accept(500, "c"));
            var slots = Forecast(index, 2, "a");

            var d = MissionControlStockAnnotation.Decide(index, 100, "c", Contract.State.Offered, Fmt, slots);

            Assert.Equal(StockUiDecorationKind.ContractAccept, d.Kind);
            Assert.True(d.Marked);
            Assert.True(MissionControlStockAnnotation.BlocksAcceptAndDecline(d));
            Assert.False(MissionControlStockAnnotation.BlocksAcceptForSlot(d));
        }

        [Fact]
        public void Decide_OnlyAnOfferedNonAutoAcceptContract_IsSlotBlocked()
        {
            var index = Index(Accept(500, "c"));
            var slots = Forecast(index, 2, "a");

            var active = MissionControlStockAnnotation.Decide(index, 100, "x", Contract.State.Active, Fmt, slots);
            var completed = MissionControlStockAnnotation.Decide(index, 100, "x", Contract.State.Completed, Fmt, slots);
            var auto = MissionControlStockAnnotation.Decide(index, 100, "x", Contract.State.Offered, Fmt, slots, autoAccept: true);
            var noModel = MissionControlStockAnnotation.Decide(index, 100, "x", Contract.State.Offered, Fmt, null);
            var roomy = MissionControlStockAnnotation.Decide(index, 100, "x", Contract.State.Offered, Fmt, Forecast(index, 3, "a"));

            Assert.False(active.Blocked);
            Assert.False(completed.Blocked);
            Assert.False(auto.Blocked);
            Assert.False(noModel.Blocked);
            Assert.False(roomy.Blocked);
        }

        // ---------------------------------------------------------------- texts

        [Fact]
        public void SlotText_NamesTheAgent_SoOffersSharingATitleAreTellable()
        {
            // First in-game census (2026-09-25): three Offered rows read "Conduct a focused
            // observational survey of Kerbin."; the agent is what Mission Control shows beside each.
            var withAgent = ReservationExplanation.ContractSlot(
                new CommittedFutureEntry(CommittedFutureKind.ContractAccept, "k", 11400, null, null,
                    title: "Conduct a focused observational survey of Kerbin.", agentTitle: "Zaltonic Electronics"), Fmt);
            var agentNoTitle = ReservationExplanation.ContractSlot(
                new CommittedFutureEntry(CommittedFutureKind.ContractAccept, "k", 11400, "rec", "Mun Lander 3",
                    agentTitle: "Zaltonic Electronics"), Fmt);

            Assert.Equal("Your committed timeline accepts the contract 'Conduct a focused observational survey of Kerbin.'"
                + " from Zaltonic Electronics on D114 and needs this slot. " + Rule + " " + WayOut, withAgent.Body);
            Assert.Equal("The committed flight 'Mun Lander 3' accepts a contract from Zaltonic Electronics on D114"
                + " and needs this slot. " + Rule + " " + WayOut, agentNoTitle.Body);
            Assert.Equal("Slot needed on D114", withAgent.Title);
        }

        [Fact]
        public void AgentTitle_IsReadForAcceptRowsOnly()
        {
            var index = CommittedFutureIndex.Build(
                new[] { Accept(500, "c"), Complete(600, "c") }, null, null, null, null,
                id => id == "c" ? "Zaltonic Electronics" : null);

            Assert.Equal("Zaltonic Electronics", index.FirstFuture(CommittedFutureKind.ContractAccept, "c", 100).AgentTitle);
            Assert.Null(index.FirstFuture(CommittedFutureKind.ContractComplete, "c", 100).AgentTitle);
            Assert.Null(CommittedFutureIndex.Build(new[] { Accept(500, "c") }, null, null, null)
                .FirstFuture(CommittedFutureKind.ContractAccept, "c", 100).AgentTitle);
        }

        [Fact]
        public void AgentTitle_ComesFromTheAcceptSnapshot_AndReachesTheSlotReason()
        {
            var node = new ConfigNode("CONTRACT");
            node.AddValue("agent", "Zaltonic Electronics");
            node.AddValue("agentName", "Zaltonic Electronics");
            GameStateStore.AddContractSnapshot("c-agent", node, 50);
            Ledger.AddAction(Accept(500, "c-agent", null, "Conduct a focused observational survey of Kerbin."));

            var index = CommittedFutureIndexCache.Current;
            var slots = Forecast(index, 2, "a");
            var d = MissionControlStockAnnotation.Decide(index, 100, "free", Contract.State.Offered, Fmt, slots);

            Assert.Equal("Zaltonic Electronics", CommittedFutureIndexCache.AgentTitleOfSnapshotNode(node));
            Assert.Null(CommittedFutureIndexCache.AgentTitleOfSnapshotNode(new ConfigNode("CONTRACT")));
            Assert.Null(CommittedFutureIndexCache.AgentTitleOfSnapshotNode(null));
            Assert.Equal("Zaltonic Electronics",
                index.FirstFuture(CommittedFutureKind.ContractAccept, "c-agent", 100).AgentTitle);
            Assert.True(MissionControlStockAnnotation.BlocksAcceptForSlot(d));
            Assert.Contains("'Conduct a focused observational survey of Kerbin.' from Zaltonic Electronics on D5", d.Why);
        }

        [Fact]
        public void SlotText_NamesTheFlightAndTheContractWhenKnown_AndIsPlainAscii()
        {
            var named = ReservationExplanation.ContractSlot(
                new CommittedFutureEntry(CommittedFutureKind.ContractAccept, "k", 11400, "rec", "Mun Lander 3", title: "Rescue Bill"), Fmt);
            var ksc = ReservationExplanation.ContractSlot(
                new CommittedFutureEntry(CommittedFutureKind.ContractAccept, "k", 11400, null, null), Fmt);
            var unnamedFlight = ReservationExplanation.ContractSlot(
                new CommittedFutureEntry(CommittedFutureKind.ContractAccept, "k", 11400, "rec2", null, title: "Orbit the Mun"), Fmt);

            Assert.Equal("Slot needed on D114", named.Title);
            Assert.Equal("The committed flight 'Mun Lander 3' accepts the contract 'Rescue Bill' on D114 and needs this slot. "
                + Rule + " " + WayOut, named.Body);
            Assert.Equal("Your committed timeline accepts a contract on D114 and needs this slot. " + Rule + " " + WayOut,
                ksc.Body);
            Assert.Equal("A committed flight accepts the contract 'Orbit the Mun' on D114 and needs this slot. "
                + Rule + " " + WayOut, unnamedFlight.Body);
            foreach (char c in named.Body + ksc.Body + unnamedFlight.Body + named.Title)
                Assert.True(c < 128, "non-ASCII char " + (int)c);
        }

        [Fact]
        public void DetailHeading_ForASlot_NamesOnlyAccept()
        {
            Assert.Equal("Accept is unavailable",
                MissionControlStockAnnotation.DetailHeadingFor(StockUiDecorationKind.ContractSlot));
            Assert.Equal(MissionControlStockAnnotation.DetailHeading,
                MissionControlStockAnnotation.DetailHeadingFor(StockUiDecorationKind.ContractAccept));
        }

        // ---------------------------------------------------------------- pairing

        /// <summary>
        /// The pairing rule for C2: the contracts whose Accept is slot-refused, the contracts
        /// whose detail panel greys Accept, the Contract.Accept backstop refusals and the CC
        /// CanAccept refusals are ONE set, over the ledger-backed cached index and the live
        /// forecast seam, and the backstop dialog says what the panel says. Decline stays
        /// stock's for a slot-refused offer.
        /// </summary>
        [Theory]
        [InlineData(2, 1)]  // committed accept at 500 fills the last slot
        [InlineData(3, 1)]  // room to spare: nothing slot-refused
        [InlineData(2, 2)]  // no slot free now: stock's own rule, nothing slot-refused
        public void Pairing_SlotRefusal_GreyedAccept_Backstop_CcCanAccept_AreOneSet(int limit, int activeCount)
        {
            Ledger.AddAction(Accept(500, "c-500"));
            var active = Enumerable.Range(0, activeCount).Select(i => "act-" + i).ToArray();
            ContractSlotReservation.ForecastProviderForTesting =
                (idx, now) => ContractSlotReservation.Forecast(idx, active, limit, now);
            var index = CommittedFutureIndexCache.Current;
            var slots = ContractSlotReservation.ForecastNow(index, 100.0);

            var offered = new[] { "c-500", "free-1", "free-2" };
            var decisions = offered.ToDictionary(id => id,
                id => MissionControlStockAnnotation.Decide(index, 100.0, id, Contract.State.Offered,
                    ReservationExplanation.DefaultDateFormatter, slots));

            var slotRefused = Set(offered.Where(id => decisions[id].Kind == StockUiDecorationKind.ContractSlot && decisions[id].Blocked));
            var greyed = Set(offered.Where(id =>
            {
                MissionControlStockUi.ResetForTesting();
                ContractSlotReservation.ForecastProviderForTesting =
                    (idx, now) => ContractSlotReservation.Forecast(idx, active, limit, now);
                return MissionControlStockUi.ResolveAcceptWrite(decisions[id], Contract.State.Offered, () => true) == false;
            }));
            var backstopRefused = Set(offered.Where(id =>
                !ContractAcceptPatch.ShouldAllowAccept(id, id, Contract.State.Offered, false, true)));
            var ccRefused = Set(offered.Where(id => !ContractConfiguratorCompat.FilterCanAccept(true, decisions[id], false)));
            var rowMarked = Set(offered.Where(id => decisions[id].Marked));

            bool reserved = limit == 2 && activeCount == 1;
            var expectedSlot = reserved ? Set(new[] { "free-1", "free-2" }) : new List<string>();
            var expectedRefused = Set(new[] { "c-500" }.Concat(expectedSlot));

            Assert.Equal(expectedSlot, slotRefused);
            Assert.Equal(expectedRefused, greyed);
            Assert.Equal(expectedRefused, backstopRefused);
            Assert.Equal(expectedRefused, ccRefused);
            // Only the committed accept carries a row mark; a slot refusal is unmarked.
            Assert.Equal(Set(new[] { "c-500" }), rowMarked);
            // Decline stays allowed for a slot-refused offer.
            Assert.True(ContractDeclinePatch.ShouldAllowDecline("free-1", "free-1"));
        }

        [Fact]
        public void Backstop_SlotRefusal_ShowsThePanelText_AndLogsTheForecast()
        {
            Ledger.AddAction(Accept(500, "c-500"));
            ContractSlotReservation.ForecastProviderForTesting =
                (idx, now) => ContractSlotReservation.Forecast(idx, new[] { "a" }, 2, now);
            var index = CommittedFutureIndexCache.Current;
            var d = MissionControlStockAnnotation.Decide(index, 100.0, "free", Contract.State.Offered,
                ReservationExplanation.DefaultDateFormatter, ContractSlotReservation.ForecastNow(index, 100.0));

            bool allowed = ContractAcceptPatch.ShouldAllowAccept("free", "Free Contract", Contract.State.Offered, false, true);

            Assert.False(allowed);
            Assert.Equal(1, dialogCount);
            Assert.Equal("Cannot accept \"Free Contract\"", dialogAction);
            Assert.Equal(d.Why, dialogReason);
            Assert.EndsWith("\n" + dialogReason, MissionControlStockAnnotation.ComposeDetailText("stock", d));
            Assert.Contains(logLines, l => l.Contains("[ContractAcceptPatch]")
                && l.Contains("blocking accept for guid=free - the committed timeline needs every free contract slot")
                && l.Contains("blocksNewAccept=true starvedAccept=c-500@500"));
        }

        [Fact]
        public void Backstop_BypassesReplay_AutoAccept_AndTheSlotFreeNowCase()
        {
            Ledger.AddAction(Accept(500, "c-500"));
            ContractSlotReservation.ForecastProviderForTesting =
                (idx, now) => ContractSlotReservation.Forecast(idx, new[] { "a" }, 2, now);

            Assert.True(ContractAcceptPatch.ShouldAllowAccept("auto", "Auto", Contract.State.Offered, true, true));
            Assert.True(ContractAcceptPatch.ShouldAllowAccept("free", "Free", Contract.State.Offered, false, false));
            GameStateRecorder.IsReplayingActions = true;
            Assert.True(ContractAcceptPatch.ShouldAllowAccept("free", "Free", Contract.State.Offered, false, true));
            GameStateRecorder.IsReplayingActions = false;
            Assert.Equal(0, dialogCount);
        }

        [Fact]
        public void LiveForecast_WithoutStockContractState_ReservesNothing_WithoutAWarn()
        {
            Ledger.AddAction(Accept(500, "c-500"));

            var forecast = ContractSlotReservation.ForecastNow();
            bool allowed = ContractAcceptPatch.ShouldAllowAccept("free", "Free", Contract.State.Offered, false, true);

            Assert.Null(forecast);
            Assert.True(allowed);
            Assert.DoesNotContain(logLines, l => l.Contains("[WARN]"));
        }

        [Fact]
        public void PassLog_SummarizesSlotRefusals_InOneLine()
        {
            Ledger.AddAction(Accept(500, "c-500"));
            ContractSlotReservation.ForecastProviderForTesting =
                (idx, now) => ContractSlotReservation.Forecast(idx, new[] { "a" }, 2, now);
            var committed = new FakeContract("Committed", Guid.NewGuid(), Contract.State.Offered);
            Ledger.AddAction(Accept(600, committed.ContractGuid.ToString()));
            var free1 = new FakeContract("Free One", Guid.NewGuid(), Contract.State.Offered);
            var free2 = new FakeContract("Free Two", Guid.NewGuid(), Contract.State.Offered);

            MissionControlStockUi.BeginRebuildPass(MissionControl.DisplayMode.Available);
            string committedLabel = MissionControlStockUi.LabelForAddItem(committed, "");
            string label1 = MissionControlStockUi.LabelForAddItem(free1, "");
            string label2 = MissionControlStockUi.LabelForAddItem(free2, "");
            MissionControlStockUi.EndRebuildPass();

            Assert.True(MissionControlStockAnnotation.HasRowStatus(committedLabel));
            Assert.Equal("", label1);
            Assert.Equal("", label2);
            Assert.Contains(logLines, l => l.Contains("decorate screen=MissionControl tab=Available items=3 marked=1 blocked=3"));
            Assert.Single(logLines, l => l.Contains("decorate screen=MissionControl kind=ContractSlot blocked=2 marked=0 why="));
            Assert.DoesNotContain(logLines, l => l.Contains("item=" + free1.ContractGuid));
        }

        // ---------------------------------------------------------------- Accept restore

        [Fact]
        public void AcceptRestore_AfterASlotBlockedSelection_GivesStocksRuleBackOnce()
        {
            var index = Index(Accept(500, "c"));
            var blocked = MissionControlStockAnnotation.Decide(index, 100, "free-1", Contract.State.Offered, Fmt, Forecast(index, 2, "a"));
            // The slot block lifts (a slot freed, or the committed accept went away).
            var lifted = MissionControlStockAnnotation.Decide(index, 100, "free-2", Contract.State.Offered, Fmt, Forecast(index, 3, "a"));
            Assert.True(MissionControlStockAnnotation.BlocksAcceptForSlot(blocked));
            Assert.False(lifted.Blocked);

            Assert.Equal(false, MissionControlStockUi.ResolveAcceptWrite(blocked, Contract.State.Offered, () => true));
            Assert.True(MissionControlStockUi.AcceptDisabledByParsekForTesting);
            Assert.Equal(true, MissionControlStockUi.ResolveAcceptWrite(lifted, Contract.State.Offered, () => true));
            Assert.False(MissionControlStockUi.AcceptDisabledByParsekForTesting);
            // Restored once: the next unblocked selection leaves stock's state alone.
            Assert.Null(MissionControlStockUi.ResolveAcceptWrite(lifted, Contract.State.Offered, () => true));
        }

        [Fact]
        public void AcceptRestore_ThroughAnActiveSelection_AndAfterAStockRefresh_NoLeak()
        {
            var index = Index(Accept(500, "c"));
            var slotBlocked = MissionControlStockAnnotation.Decide(index, 100, "free-1", Contract.State.Offered, Fmt, Forecast(index, 2, "a"));
            var free = MissionControlStockAnnotation.Decide(index, 100, "free-2", Contract.State.Offered, Fmt, null);
            var active = MissionControlStockAnnotation.Decide(index, 100, "a", Contract.State.Active, Fmt, Forecast(index, 2, "a"));
            Assert.False(active.Blocked);

            // Slot-blocked, then an Active row (Accept hidden): the restore waits.
            MissionControlStockUi.ResolveAcceptWrite(slotBlocked, Contract.State.Offered, () => true);
            Assert.Null(MissionControlStockUi.ResolveAcceptWrite(active, Contract.State.Active, () => true));
            Assert.True(MissionControlStockUi.AcceptDisabledByParsekForTesting);
            // Stock's own full-slot answer is what comes back, not a blanket true.
            Assert.Equal(false, MissionControlStockUi.ResolveAcceptWrite(free, Contract.State.Offered, () => false));

            // Slot-blocked, then stock RefreshUIControls rewrites Accept itself (the postfix
            // clears the flag): no later restore overrides stock's fresh answer.
            MissionControlStockUi.ResolveAcceptWrite(slotBlocked, Contract.State.Offered, () => true);
            MissionControlStockUi.ReapplyButtonsForSelection(null, "test");
            Assert.False(MissionControlStockUi.AcceptDisabledByParsekForTesting);
            Assert.Null(MissionControlStockUi.ResolveAcceptWrite(free, Contract.State.Offered, () => true));
        }

        private static List<string> Set(IEnumerable<string> ids) =>
            ids.Distinct(StringComparer.Ordinal).OrderBy(x => x, StringComparer.Ordinal).ToList();
    }
}
