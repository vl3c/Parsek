using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using Parsek.Patches;
using Xunit;

namespace Parsek.Tests
{
    /// <summary>
    /// The per-screen decoration queries (StockUiDecorationQuery.cs), their log lines, and
    /// the E18-style pairing cells: for every clickable kind the set of items the screen
    /// MARKS equals the set the click-block REFUSES, both driven through one index.
    /// </summary>
    [Collection("Sequential")]
    public class StockUiDecorationQueryTests : IDisposable
    {
        private readonly List<string> logLines = new List<string>();
        private bool dialogShown;
        private string dialogReason;

        private static readonly Func<double, string> Fmt =
            ut => "D" + ((long)(ut / 100.0)).ToString(CultureInfo.InvariantCulture);

        public StockUiDecorationQueryTests()
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
            ParsekScenario.ResetInstanceForTesting();
            CommittedActionDialog.TestHookForTesting = (action, reason, detail) =>
            {
                dialogShown = true;
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
            ParsekScenario.ResetInstanceForTesting();
            ParsekLog.ResetTestOverrides();
        }

        private static CommittedFutureIndex Index(params GameAction[] actions)
        {
            return CommittedFutureIndex.Build(actions, id => true, id => id == "rec" ? "Mun Lander 3" : null, null);
        }

        private static GameAction Tech(double ut, string id) =>
            new GameAction { UT = ut, Type = GameActionType.ScienceSpending, NodeId = id, Cost = 10f };
        private static GameAction Accept(double ut, string id) =>
            new GameAction { UT = ut, Type = GameActionType.ContractAccept, ContractId = id };
        private static GameAction Hire(double ut, string name) =>
            new GameAction { UT = ut, Type = GameActionType.KerbalHire, KerbalName = name };
        private static GameAction Upgrade(double ut, string id, int level) =>
            new GameAction { UT = ut, Type = GameActionType.FacilityUpgrade, FacilityId = id, ToLevel = level };

        // ---------------- R&D ----------------

        [Fact]
        public void ForRnD_MarksAndBlocksOnlyNodesWithACommittedFutureRow()
        {
            var index = Index(Tech(500, "ahead"), Tech(50, "past"));

            var d = StockUiDecorationQuery.ForRnD(index, 100, new[] { "ahead", "past", "plain" }, Fmt);

            Assert.Equal(3, d.Count);
            var ahead = d.Single(x => x.Id == "ahead");
            Assert.True(ahead.Marked);
            Assert.True(ahead.Blocked);
            Assert.Equal(StockUiDecorationKind.TechResearch, ahead.Kind);
            Assert.Equal(StockUiScreen.RnD, ahead.Screen);
            Assert.Equal("Tree", ahead.Tab);
            Assert.Equal(500.0, ahead.UT);
            Assert.StartsWith("Researched on D5 on your committed timeline.", ahead.Why);
            Assert.All(d.Where(x => x.Id != "ahead"), x =>
            {
                Assert.False(x.Marked);
                Assert.False(x.Blocked);
                Assert.Equal(StockUiDecorationKind.None, x.Kind);
                Assert.Null(x.Why);
            });
        }

        [Fact]
        public void ForRnD_DuplicateRows_UseTheEarliestUT()
        {
            var index = Index(Tech(900, "dupe"), Tech(500, "dupe"));
            var d = StockUiDecorationQuery.ForRnD(index, 100, new[] { "dupe" }, Fmt).Single();
            Assert.Equal(500.0, d.UT);
        }

        [Fact]
        public void ForRnD_EmptyIndex_MarksNothing()
        {
            var d = StockUiDecorationQuery.ForRnD(CommittedFutureIndex.Empty, 0, new[] { "a", "b" }, Fmt);
            Assert.DoesNotContain(d, x => x.Marked || x.Blocked);
        }

        // ---------------- Mission Control ----------------

        [Fact]
        public void ForMissionControl_DecoratesOnlyTheAvailableTab()
        {
            var index = Index(Accept(500, "c-offered"), Accept(500, "c-active"));

            var d = StockUiDecorationQuery.ForMissionControl(index, 100, new[]
            {
                new StockUiItem("c-offered", "Available"),
                new StockUiItem("c-active", "Active"),
                new StockUiItem("c-other", "Available")
            }, Fmt);

            Assert.True(d.Single(x => x.Id == "c-offered").Marked);
            Assert.True(d.Single(x => x.Id == "c-offered").Blocked);
            Assert.Equal("Accepted on D5", d.Single(x => x.Id == "c-offered").Title);
            // An already-active contract is not offered again: no mark (the Active-row
            // annotation is a later PR).
            Assert.False(d.Single(x => x.Id == "c-active").Marked);
            Assert.False(d.Single(x => x.Id == "c-other").Marked);
        }

        // ---------------- Astronaut Complex ----------------

        private static AstronautComplexContext Context(
            Func<string, KerbalReservationKind> kind = null,
            Func<string, KerbalsModule.KerbalReservation> reservation = null,
            Func<string, string> owner = null,
            Func<string, bool> dismissal = null,
            ISet<string> live = null)
        {
            return new AstronautComplexContext
            {
                ReservationKind = kind,
                Reservation = reservation,
                SlotOwner = owner,
                DismissalBlocked = dismissal,
                IsLoopingRecording = id => false,
                LiveCrewOrTourist = live
            };
        }

        [Fact]
        public void ForAstronautComplex_FutureHireWinsOverReserved_AndIsBlocked()
        {
            var index = Index(Hire(500, "Val Kerman"));
            var d = StockUiDecorationQuery.ForAstronautComplex(index, 100,
                new[] { new StockUiItem("Val Kerman", "Applicants") },
                Context(kind: _ => KerbalReservationKind.ReservedActive), Fmt).Single();

            Assert.Equal(StockUiDecorationKind.KerbalHire, d.Kind);
            Assert.True(d.Marked);
            Assert.True(d.Blocked);
            Assert.Equal("Hired on D5", d.Title);
        }

        [Fact]
        public void ForAstronautComplex_AlreadyLiveFutureHire_FallsThroughToTheNextKind()
        {
            var index = Index(Hire(500, "Live Kerman"));
            var d = StockUiDecorationQuery.ForAstronautComplex(index, 100,
                new[] { new StockUiItem("Live Kerman", "Available") },
                Context(live: new HashSet<string> { "Live Kerman" }), Fmt).Single();

            Assert.Equal(StockUiDecorationKind.None, d.Kind);
            Assert.False(d.Marked);
        }

        [Fact]
        public void ForAstronautComplex_FutureDismissal_FromTheMilestoneFallback()
        {
            var index = CommittedFutureIndex.Build(null, null, null, new[]
            {
                new CommittedFutureEntry(CommittedFutureKind.KerbalRetire, "Retiree Kerman", 300, null, null, fromMilestoneFallback: true)
            });
            var d = StockUiDecorationQuery.ForAstronautComplex(index, 100,
                new[] { new StockUiItem("Retiree Kerman", "Available") },
                Context(dismissal: _ => true), Fmt).Single();

            Assert.Equal(StockUiDecorationKind.KerbalRetire, d.Kind);
            Assert.True(d.Marked);
            Assert.True(d.Blocked);
            Assert.Equal(300.0, d.UT);
            Assert.Equal("Dismissed on D3", d.Title);
        }

        [Fact]
        public void ForAstronautComplex_Reserved_NamesTheHoldingFlightAndWhenItFrees()
        {
            var index = CommittedFutureIndex.Build(
                new[]
                {
                    new GameAction { UT = 10, Type = GameActionType.KerbalAssignment, KerbalName = "Jeb", RecordingId = "rec",
                        KerbalEndStateField = KerbalEndState.Recovered, EndUT = 13000f }
                },
                id => true, id => "Mun Lander 3", null);
            var reservation = new KerbalsModule.KerbalReservation { KerbalName = "Jeb", ReservedUntilUT = 13000 };

            var d = StockUiDecorationQuery.ForAstronautComplex(index, 100,
                new[] { new StockUiItem("Jeb", "Assigned") },
                Context(kind: _ => KerbalReservationKind.ReservedActive, reservation: _ => reservation,
                    owner: n => n, dismissal: _ => true), Fmt).Single();

            Assert.Equal(StockUiDecorationKind.KerbalOnFlight, d.Kind);
            Assert.True(d.Marked);
            Assert.True(d.Blocked);
            Assert.Equal("Reserved until D130", d.Title);
            Assert.Equal("Flies 'Mun Lander 3' on your committed timeline. "
                + ReservationExplanation.CrewRule + " Free after D130.", d.Why);
        }

        [Fact]
        public void ForAstronautComplex_PermanentReservation_ReadsLost()
        {
            var index = CommittedFutureIndex.Build(
                new[]
                {
                    new GameAction { UT = 10, Type = GameActionType.KerbalAssignment, KerbalName = "Jeb", RecordingId = "rec",
                        KerbalEndStateField = KerbalEndState.Dead, EndUT = 200f }
                },
                id => true, id => "Mun Lander 3", null);
            var reservation = new KerbalsModule.KerbalReservation
            {
                KerbalName = "Jeb", ReservedUntilUT = double.PositiveInfinity, IsPermanent = true
            };

            var d = StockUiDecorationQuery.ForAstronautComplex(index, 100,
                new[] { new StockUiItem("Jeb", "Kia") },
                Context(kind: _ => KerbalReservationKind.ReservedActive, reservation: _ => reservation), Fmt).Single();

            Assert.Equal(StockUiDecorationKind.KerbalLost, d.Kind);
            Assert.Equal("Lost", d.Title);
            Assert.StartsWith("Lost on the committed flight 'Mun Lander 3'.", d.Why);
        }

        [Fact]
        public void ForAstronautComplex_RetiredStandIn_AndAnUnmarkedDismissalBlock()
        {
            var d = StockUiDecorationQuery.ForAstronautComplex(CommittedFutureIndex.Empty, 100,
                new[] { new StockUiItem("Lars Kerman", "Available"), new StockUiItem("Chain Kerman", "Available") },
                Context(kind: n => n == "Lars Kerman" ? KerbalReservationKind.ReservedRetired : KerbalReservationKind.NotManaged,
                    dismissal: _ => true), Fmt);

            var lars = d.Single(x => x.Id == "Lars Kerman");
            Assert.Equal(StockUiDecorationKind.KerbalRetiredStandIn, lars.Kind);
            Assert.Equal(StockUiDecorationQuery.RetiredStandInText, lars.Why);
            // A stand-in in a replacement chain is refused dismissal with no mark today.
            var chain = d.Single(x => x.Id == "Chain Kerman");
            Assert.False(chain.Marked);
            Assert.True(chain.Blocked);
        }

        /// <summary>
        /// E10: committed-future overlays are unconditional - the ShouldApplyOverlays gate
        /// was deleted with the showCommittedFutureOverlays setting.
        /// </summary>
        [Fact]
        public void E10_OverlayGateStaysDeleted()
        {
            Assert.Null(typeof(StockUiOverlayController).GetMethod(
                "ShouldApplyOverlays",
                System.Reflection.BindingFlags.Static
                | System.Reflection.BindingFlags.Public
                | System.Reflection.BindingFlags.NonPublic));
        }

        [Theory]
        [InlineData(Contracts.Contract.State.Offered, "Available")]
        [InlineData(Contracts.Contract.State.Active, "Active")]
        [InlineData(Contracts.Contract.State.Completed, "Archive")]
        public void MissionControlTabFor_MapsTheContractState(Contracts.Contract.State state, string tab)
        {
            Assert.Equal(tab, StockUiOverlayController.MissionControlTabFor(state));
        }

        // ---------------- logging ----------------

        [Fact]
        public void LogPass_OneInfoLinePerTab_AndOneVerboseLinePerDecoratedItem()
        {
            var index = Index(Tech(500, "ahead"));
            var d = StockUiDecorationQuery.ForRnD(index, 100, new[] { "ahead", "plain1", "plain2" }, Fmt);

            StockUiDecorationQuery.LogPass(StockUiScreen.RnD, new[] { "Tree" }, d);

            Assert.Contains(logLines, l => l.Contains("[INFO][StockUiOverlay]")
                && l.EndsWith("decorate screen=RnD tab=Tree items=3 marked=1 blocked=1"));
            Assert.Contains(logLines, l => l.Contains("[VERBOSE][StockUiOverlay]")
                && l.Contains("decorate screen=RnD tab=Tree item=ahead kind=TechResearch marked=true blocked=true why=\"Researched on D5"));
            Assert.DoesNotContain(logLines, l => l.Contains("item=plain1"));
        }

        [Fact]
        public void LogPass_ListedTabWithNoItems_StillLogsZeroes()
        {
            StockUiDecorationQuery.LogPass(StockUiScreen.MissionControl, new[] { "Available" },
                new List<StockUiDecoration>());
            Assert.Contains(logLines, l => l.Contains("[INFO][StockUiOverlay]")
                && l.EndsWith("decorate screen=MissionControl tab=Available items=0 marked=0 blocked=0"));
        }

        // ---------------- pairing (E18): mark set == block set ----------------

        private static HashSet<string> MarkedIds(IEnumerable<StockUiDecoration> d) =>
            new HashSet<string>(d.Where(x => x.Marked).Select(x => x.Id), StringComparer.Ordinal);

        private static HashSet<string> FilterIds(IEnumerable<string> ids, Func<string, bool> predicate) =>
            new HashSet<string>(ids.Where(predicate), StringComparer.Ordinal);

        [Theory]
        [InlineData(0.0)]
        [InlineData(150.0)]
        [InlineData(250.0)]
        [InlineData(1000.0)]
        public void Pairing_TechResearch_MarkSetEqualsBlockSet(double now)
        {
            var index = Index(Tech(100, "t1"), Tech(200, "t2"), Tech(300, "t3"));
            var ids = new[] { "t1", "t2", "t3", "t4" };

            var marked = MarkedIds(StockUiDecorationQuery.ForRnD(index, now, ids, Fmt));
            var blocked = FilterIds(ids, id => StockUiReservationPredicates.IsTechResearchBlocked(index, id, now));

            Assert.Equal(blocked.OrderBy(x => x), marked.OrderBy(x => x));
            Assert.Equal(index.FutureKeys(CommittedFutureKind.TechResearch, now).OrderBy(x => x), marked.OrderBy(x => x));
        }

        [Theory]
        [InlineData(0.0)]
        [InlineData(250.0)]
        [InlineData(1000.0)]
        public void Pairing_ContractAcceptAndDecline_MarkSetEqualsBlockSet(double now)
        {
            var index = Index(Accept(100, "c1"), Accept(300, "c2"));
            var ids = new[] { "c1", "c2", "c3" };

            var marked = MarkedIds(StockUiDecorationQuery.ForMissionControl(index, now,
                ids.Select(id => new StockUiItem(id, "Available")), Fmt));
            // Decline (section 7.2) reads the accept predicate on the same row.
            var blocked = FilterIds(ids, id => StockUiReservationPredicates.IsContractAcceptBlocked(index, id, now));

            Assert.Equal(blocked.OrderBy(x => x), marked.OrderBy(x => x));
        }

        [Theory]
        [InlineData(0.0)]
        [InlineData(250.0)]
        [InlineData(1000.0)]
        public void Pairing_KerbalHire_MarkSetEqualsBlockSet(double now)
        {
            var index = Index(Hire(100, "A Kerman"), Hire(300, "B Kerman"));
            var ids = new[] { "A Kerman", "B Kerman", "C Kerman" };

            var marked = new HashSet<string>(StockUiDecorationQuery.ForAstronautComplex(index, now,
                    ids.Select(id => new StockUiItem(id, "Applicants")),
                    Context(kind: n => n == "C Kerman" ? KerbalReservationKind.ReservedActive : KerbalReservationKind.NotManaged),
                    Fmt)
                .Where(x => x.Kind == StockUiDecorationKind.KerbalHire && x.Marked).Select(x => x.Id));
            var blocked = FilterIds(ids, id => StockUiReservationPredicates.IsKerbalHireBlocked(index, id, now));

            Assert.Equal(blocked.OrderBy(x => x), marked.OrderBy(x => x));
        }

        [Theory]
        [InlineData(0.0)]
        [InlineData(250.0)]
        [InlineData(1000.0)]
        public void Pairing_FacilityUpgrade_MarkSetEqualsBlockSet(double now)
        {
            // No stock screen marks facilities yet (the KSC facility menu is a later PR);
            // its mark will read FutureKeys, so pin that it equals the block predicate.
            var index = Index(Upgrade(100, "F1", 2), Upgrade(300, "F2", 2), Upgrade(200, "F2", 3));
            var ids = new[] { "F1", "F2", "F3" };

            var marked = index.FutureKeys(CommittedFutureKind.FacilityUpgrade, now);
            var blocked = FilterIds(ids, id => StockUiReservationPredicates.IsFacilityUpgradeBlocked(index, id, now));

            Assert.Equal(blocked.OrderBy(x => x), marked.OrderBy(x => x));
        }

        // Through the live patches: the patch refuses exactly the items the query marks,
        // over the ledger-backed cached index, and the dialog says what the hover says.

        [Fact]
        public void Pairing_ContractAcceptPatch_RefusesExactlyTheMarkedRows_WithTheSameText()
        {
            Ledger.AddAction(Accept(500, "c-committed"));
            Ledger.AddAction(Accept(50, "c-past"));
            CommittedFutureIndexCache.NowUtProviderForTesting = () => 100;
            var ids = new[] { "c-committed", "c-past", "c-free" };

            var decorations = StockUiDecorationQuery.ForMissionControl(CommittedFutureIndexCache.Current, 100,
                ids.Select(id => new StockUiItem(id, "Available")), ReservationExplanation.DefaultDateFormatter);
            var refused = FilterIds(ids, id => !ContractAcceptPatch.ShouldAllowAccept(id, id));

            Assert.Equal(new[] { "c-committed" }, MarkedIds(decorations).ToArray());
            Assert.Equal(new[] { "c-committed" }, refused.ToArray());

            dialogReason = null;
            ContractAcceptPatch.ShouldAllowAccept("c-committed", "Committed");
            Assert.Equal(decorations.Single(x => x.Id == "c-committed").Why, dialogReason);
        }

        [Fact]
        public void Pairing_KerbalHirePatch_RefusesExactlyTheMarkedApplicants_WithTheSameText()
        {
            Ledger.AddAction(Hire(500, "Future Kerman"));
            Ledger.AddAction(Hire(50, "Past Kerman"));
            CommittedFutureIndexCache.NowUtProviderForTesting = () => 100;
            var ids = new[] { "Future Kerman", "Past Kerman", "Free Kerman" };

            var decorations = StockUiDecorationQuery.ForAstronautComplex(CommittedFutureIndexCache.Current, 100,
                ids.Select(id => new StockUiItem(id, "Applicants")), Context(), ReservationExplanation.DefaultDateFormatter);
            var refused = FilterIds(ids, id => !KerbalHirePatch.ShouldAllowHire(id));

            Assert.Equal(new[] { "Future Kerman" }, MarkedIds(decorations).ToArray());
            Assert.Equal(new[] { "Future Kerman" }, refused.ToArray());
            Assert.True(dialogShown);
            Assert.Equal(decorations.Single(x => x.Id == "Future Kerman").Why, dialogReason);
        }

        [Fact]
        public void Patches_BypassWhileReplayingActions()
        {
            Ledger.AddAction(Accept(500, "c1"));
            Ledger.AddAction(Hire(500, "H Kerman"));
            CommittedFutureIndexCache.NowUtProviderForTesting = () => 100;
            GameStateRecorder.IsReplayingActions = true;

            Assert.True(ContractAcceptPatch.ShouldAllowAccept("c1", "c1"));
            Assert.True(KerbalHirePatch.ShouldAllowHire("H Kerman"));
            Assert.False(dialogShown);
        }
    }
}
