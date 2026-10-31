using System;
using System.Collections.Generic;
using System.Linq;
using Xunit;

namespace Parsek.Tests
{
    /// <summary>
    /// A Space Center firing stays on the timeline like a hire (owner ruling 2026-10-28,
    /// option A; design section 9.12.1; todo KERBAL-DISMISSAL-ON-THE-TIMELINE): the
    /// <c>CrewDismissed</c> capture and its <c>KerbalDismissal</c> row, the pass that makes a
    /// reached dismissal once, the blocks before it (Dismiss and crew assignment), the
    /// Astronaut Complex / crew dialog text, and the hire ownership a dismissal ends.
    /// </summary>
    [Collection("Sequential")]
    public class KerbalDismissalTimelineTests : IDisposable
    {
        private readonly List<string> logLines = new List<string>();

        private static readonly Func<double, string> Fmt = ut => "D" + ((int)(ut / 100)).ToString();

        public KerbalDismissalTimelineTests()
        {
            ParsekLog.ResetTestOverrides();
            ParsekLog.SuppressLogging = false;
            ParsekLog.VerboseOverrideForTesting = true;
            ParsekLog.TestSinkForTesting = line => logLines.Add(line);

            RecordingStore.SuppressLogging = true;
            RecordingStore.ResetForTesting();
            KspStatePatcher.SuppressUnityCallsForTesting = true;
            GameStateStore.SuppressLogging = true;
            GameStateStore.ResetForTesting();
            MilestoneStore.ResetForTesting();
            LedgerOrchestrator.ResetForTesting();
            CommittedFutureIndexCache.ResetForTesting();
        }

        public void Dispose()
        {
            CommittedFutureIndexCache.ResetForTesting();
            LedgerOrchestrator.ResetForTesting();
            KspStatePatcher.ResetForTesting();
            MilestoneStore.ResetForTesting();
            RecordingStore.ResetForTesting();
            RecordingStore.SuppressLogging = false;
            GameStateStore.ResetForTesting();
            ParsekLog.ResetTestOverrides();
            ParsekLog.SuppressLogging = true;
        }

        private static GameAction Hire(double ut, string name, long cseq = 0, string recordingId = null)
        {
            return new GameAction
            {
                UT = ut, Type = GameActionType.KerbalHire, KerbalName = name, HireCost = 100f,
                CaptureSequence = cseq, RecordingId = recordingId, ActionId = "hire-" + name + "-" + ut
            };
        }

        private static GameAction Dismiss(double ut, string name, long cseq = 0, string recordingId = null)
        {
            return new GameAction
            {
                UT = ut, Type = GameActionType.KerbalDismissal, KerbalName = name, KerbalRole = "Pilot",
                CaptureSequence = cseq, RecordingId = recordingId, ActionId = "dismiss-" + name + "-" + ut
            };
        }

        private static CommittedFutureIndex IndexOf(params GameAction[] rows)
        {
            return CommittedFutureIndex.Build(rows, _ => true, null, null);
        }

        // ---------------- capture and row ----------------

        [Fact]
        public void CrewDismissedEvent_ConvertsToAKscDismissalRow()
        {
            GameStateEvent evt = GameStateRecorder.BuildCrewDismissedEvent("Bob Kerman", "Scientist", 1000.0);
            Assert.Equal(GameStateEventType.CrewDismissed, evt.eventType);
            Assert.Equal("Bob Kerman", evt.key);

            GameAction row = GameStateEventConverter.ConvertEvent(evt, null);

            Assert.NotNull(row);
            Assert.Equal(GameActionType.KerbalDismissal, row.Type);
            Assert.Equal("Bob Kerman", row.KerbalName);
            Assert.Equal("Scientist", row.KerbalRole);
            Assert.Equal(1000.0, row.UT);
            Assert.Null(row.RecordingId);
            Assert.Equal("Dismissed Bob Kerman (Scientist)", GameStateEventDisplay.GetDisplayDescription(evt));
            Assert.Equal("Crew", GameStateEventDisplay.GetDisplayCategory(GameStateEventType.CrewDismissed));
        }

        [Fact]
        public void CrewDismissedEvent_WithNoKerbal_WritesNoRow_AndSaysSo()
        {
            var evt = new GameStateEvent { ut = 5, eventType = GameStateEventType.CrewDismissed, key = "" };
            Assert.Null(GameStateEventConverter.ConvertEvent(evt, null));
            Assert.Contains(logLines, l => l.Contains("ConvertCrewDismissed: event names no kerbal"));
        }

        [Fact]
        public void CrewRemoved_IsStillNotADismissal()
        {
            var evt = new GameStateEvent { ut = 5, eventType = GameStateEventType.CrewRemoved, key = "Bob Kerman" };
            Assert.Null(GameStateEventConverter.ConvertEvent(evt, null));
        }

        [Fact]
        public void DismissalRow_RoundTripsThroughTheLedgerFile()
        {
            var parent = new ConfigNode("LEDGER");
            Dismiss(1234.5, "Bob Kerman").SerializeInto(parent);

            GameAction restored = GameAction.DeserializeFrom(parent.GetNode("GAME_ACTION"));

            Assert.Equal(GameActionType.KerbalDismissal, restored.Type);
            Assert.Equal("Bob Kerman", restored.KerbalName);
            Assert.Equal("Pilot", restored.KerbalRole);
            Assert.Equal(1234.5, restored.UT);
        }

        [Fact]
        public void DismissalRow_DefersItsKscRecalcOneFrame_LikeAHire()
        {
            // Stock fires OnCrewmemberSacked before it turns the kerbal into an applicant.
            Assert.True(LedgerOrchestrator.ShouldDeferKscReconcileOneFrame(GameActionType.KerbalDismissal, true));
            Assert.False(LedgerOrchestrator.ShouldDeferKscReconcileOneFrame(GameActionType.KerbalDismissal, false));
        }

        [Fact]
        public void DismissalRow_IsNeverTombstoned_AndMovesNoPool()
        {
            Assert.False(TombstoneEligibility.IsSupersedeTombstoneEligible(Dismiss(10, "Bob Kerman", recordingId: "rec")));
            Assert.Equal(KscActionExpectationClassifier.KscReconcileClass.NoResourceImpact,
                KscActionExpectationClassifier.ClassifyAction(Dismiss(10, "Bob Kerman")).Class);
        }

        [Fact]
        public void DismissalRow_ReadsAsADismissOnTheTimeline()
        {
            Assert.Equal("Dismiss: Bob Kerman", GameActionDisplay.GetDescription(Dismiss(10, "Bob Kerman"), null));
            Assert.Equal(TimelineEntryType.KerbalHire, TimelineEntryDisplay.MapGameActionType(GameActionType.KerbalDismissal));

            GameAction row = Dismiss(10, "Bob Kerman");
            var evt = new GameStateEvent { ut = 10, eventType = GameStateEventType.CrewDismissed, key = "Bob Kerman" };
            Assert.Equal(TimelineBuilder.GetLegacyEventKey(evt), TimelineBuilder.GetLegacyDuplicateKey(row));
        }

        // ---------------- the reached-dismissal pass ----------------

        [Fact]
        public void Resolve_NamesAKerbalWhoseLatestReachedRosterRowIsADismissal()
        {
            var actions = new List<GameAction>
            {
                Hire(10, "Bob Kerman", 1), Dismiss(100, "Bob Kerman", 2),
                Hire(10, "Val Kerman", 3), Dismiss(50, "Val Kerman", 4), Hire(80, "Val Kerman", 5),
                Dismiss(500, "Jeb Kerman", 6)
            };

            List<string> at200 = LedgerOrchestrator.ResolveCommittedDismissalsToMake(actions, _ => true, 200);
            Assert.Equal(new[] { "Bob Kerman" }, at200.ToArray());

            // Before Bob's row he is not named; at the row's own UT he is (a row AT now is applied).
            Assert.Empty(LedgerOrchestrator.ResolveCommittedDismissalsToMake(actions, _ => true, 99));
            Assert.Equal(new[] { "Bob Kerman" },
                LedgerOrchestrator.ResolveCommittedDismissalsToMake(actions, _ => true, 100).ToArray());

            // Jeb at 600: named too, ordered by name.
            Assert.Equal(new[] { "Bob Kerman", "Jeb Kerman" },
                LedgerOrchestrator.ResolveCommittedDismissalsToMake(actions, _ => true, 600).ToArray());
        }

        [Fact]
        public void Resolve_IgnoresARowOfAnUncommittedRecording()
        {
            var actions = new List<GameAction> { Dismiss(10, "Bob Kerman", 1, recordingId: "rec-pending") };
            Assert.Empty(LedgerOrchestrator.ResolveCommittedDismissalsToMake(actions, id => id == "rec-other", 50));
            Assert.Single(LedgerOrchestrator.ResolveCommittedDismissalsToMake(actions, id => id == "rec-pending", 50));
            Assert.Empty(LedgerOrchestrator.ResolveCommittedDismissalsToMake(actions, null, 50));
        }

        [Fact]
        public void Plan_SkipsWhileARewindIsPending_OrTheClockIsNotReady()
        {
            var actions = new List<GameAction> { Dismiss(10, "Bob Kerman") };
            string reason;

            Assert.Null(LedgerOrchestrator.PlanReachedCommittedDismissals(
                actions, _ => true, 50, double.NaN, true, out reason));
            Assert.Equal(LedgerOrchestrator.DismissalSkipRewindPending, reason);

            Assert.Null(LedgerOrchestrator.PlanReachedCommittedDismissals(
                actions, _ => true, double.NaN, double.NaN, false, out reason));
            Assert.Equal(LedgerOrchestrator.DismissalSkipClockNotReady, reason);

            Assert.Null(LedgerOrchestrator.PlanReachedCommittedDismissals(
                actions, _ => true, 0.0, double.NaN, false, out reason));
            Assert.Equal(LedgerOrchestrator.DismissalSkipClockNotReady, reason);
        }

        [Fact]
        public void Plan_RaisesTheClockToTheHeldThroughUT()
        {
            // A Rewind-to-Launch lead window: the clock sits behind rows the loaded world holds.
            var actions = new List<GameAction> { Dismiss(80, "Bob Kerman") };
            string reason;

            List<string> held = LedgerOrchestrator.PlanReachedCommittedDismissals(
                actions, _ => true, 50, 100, false, out reason);
            Assert.Null(reason);
            Assert.Equal(new[] { "Bob Kerman" }, held.ToArray());

            List<string> notHeld = LedgerOrchestrator.PlanReachedCommittedDismissals(
                actions, _ => true, 50, double.NaN, false, out reason);
            Assert.Empty(notHeld);
        }

        [Fact]
        public void Apply_TurnsAnAvailableCrewKerbalIntoAnApplicant_Once()
        {
            var types = new Dictionary<string, ProtoCrewMember.KerbalType>
            {
                { "Bob Kerman", ProtoCrewMember.KerbalType.Crew },
                { "Val Kerman", ProtoCrewMember.KerbalType.Applicant },
                { "Jeb Kerman", ProtoCrewMember.KerbalType.Crew }
            };
            var statuses = new Dictionary<string, ProtoCrewMember.RosterStatus>
            {
                { "Bob Kerman", ProtoCrewMember.RosterStatus.Available },
                { "Val Kerman", ProtoCrewMember.RosterStatus.Available },
                { "Jeb Kerman", ProtoCrewMember.RosterStatus.Assigned }
            };
            var demoted = new List<string>();
            Func<string, ProtoCrewMember.KerbalType?> readType =
                n => types.TryGetValue(n, out var t) ? t : (ProtoCrewMember.KerbalType?)null;
            Func<string, ProtoCrewMember.RosterStatus?> readStatus =
                n => statuses.TryGetValue(n, out var s) ? s : (ProtoCrewMember.RosterStatus?)null;
            Func<string, bool> demote = n =>
            {
                demoted.Add(n);
                types[n] = ProtoCrewMember.KerbalType.Applicant;
                return true;
            };
            var names = new List<string> { "Bob Kerman", "Val Kerman", "Jeb Kerman", "Ghost Kerman" };

            int count = KerbalsModule.ApplyReachedCommittedDismissals(names, readType, readStatus, demote);

            Assert.Equal(1, count);
            Assert.Equal(new[] { "Bob Kerman" }, demoted.ToArray());
            Assert.Contains(logLines, l => l.Contains("[KerbalsModule]")
                && l.Contains("Committed dismissal applied: 'Bob Kerman' Crew -> Applicant"));
            Assert.Contains(logLines, l => l.Contains("[WARN]") && l.Contains("'Jeb Kerman' is Assigned")
                && l.Contains("retried next pass"));
            Assert.Contains(logLines, l => l.Contains(
                "ApplyReachedCommittedDismissals: rows=4 dismissed=1 notCrew=1 notAvailable=1 missing=1 failed=0"));

            // The next pass names Bob again and changes nothing: the dismissal is made once.
            logLines.Clear();
            Assert.Equal(0, KerbalsModule.ApplyReachedCommittedDismissals(
                new List<string> { "Bob Kerman" }, readType, readStatus, demote));
            Assert.Single(demoted);
            Assert.Contains(logLines, l => l.Contains("'Bob Kerman' already type=Applicant; nothing to do"));
        }

        // ---------------- the committed-future index and the blocks ----------------

        [Fact]
        public void Index_ClassifiesADismissalByKerbalName()
        {
            CommittedFutureKind kind;
            string key;
            Assert.True(CommittedFutureIndex.TryClassify(Dismiss(10, "Bob Kerman"), out kind, out key));
            Assert.Equal(CommittedFutureKind.KerbalDismissal, kind);
            Assert.Equal("Bob Kerman", key);

            var index = IndexOf(Dismiss(600, "Bob Kerman"));
            Assert.True(StockUiReservationPredicates.IsKerbalDismissalPending(index, "Bob Kerman", 590));
            Assert.False(StockUiReservationPredicates.IsKerbalDismissalPending(index, "Bob Kerman", 600));
            Assert.False(StockUiReservationPredicates.IsKerbalDismissalPending(index, "Val Kerman", 590));
            Assert.False(StockUiReservationPredicates.IsKerbalDismissalPending(null, "Bob Kerman", 590));
        }

        [Fact]
        public void SecondDismissal_BeforeTheFirst_IsRefusedWithTheDatedText()
        {
            var index = IndexOf(Dismiss(600, "Bob Kerman"));

            string refusal = Patches.KerbalDismissalPatch.DescribePendingDismissal(index, "Bob Kerman", 590, Fmt);
            Assert.NotNull(refusal);
            Assert.StartsWith("Dismissed on D6", refusal);
            Assert.Contains("until then", refusal);

            Assert.Null(Patches.KerbalDismissalPatch.DescribePendingDismissal(index, "Bob Kerman", 600, Fmt));
        }

        [Fact]
        public void LiveDismissalRefusal_ReadsTheLedgerRow_AndLiftsAtItsUT()
        {
            Ledger.AddAction(Dismiss(600, "Bob Kerman"));
            CommittedFutureIndexCache.NowUtProviderForTesting = () => 590;

            Assert.True(StockUiReservationPredicates.IsKerbalDismissalPendingNow("Bob Kerman"));
            Assert.NotNull(Patches.KerbalDismissalPatch.DescribeDismissalRefusal(null, "Bob Kerman"));
            Assert.False(StockUiReservationPredicates.IsKerbalDismissalPendingNow("Val Kerman"));

            CommittedFutureIndexCache.NowUtProviderForTesting = () => 600;
            Assert.False(StockUiReservationPredicates.IsKerbalDismissalPendingNow("Bob Kerman"));
            Assert.Null(Patches.KerbalDismissalPatch.DescribeDismissalRefusal(null, "Bob Kerman"));
        }

        [Fact]
        public void CrewDialog_RefusesAKerbalDismissedLater_WithTheSameText()
        {
            var index = IndexOf(Dismiss(600, "Bob Kerman"));

            StockUiDecoration d = StockUiCrewDialogDecoration.Decide(
                "Bob Kerman", true, KerbalReservationKind.NotManaged, index, null, Fmt, 590);

            Assert.Equal(StockUiDecorationKind.KerbalDismissal, d.Kind);
            Assert.True(d.Marked);
            Assert.True(d.Blocked);
            Assert.Equal("Dismissed on D6", d.Title);
            Assert.Equal(600.0, d.UT);
            Assert.Equal(StockUiReservationPredicates.ExplainKerbalDismissal(index, "Bob Kerman", 590, Fmt).Body, d.Why);
            Assert.Equal("Dismissed on D6", StockUiCrewDialogDecoration.TraitLineStatus(d));
        }

        [Fact]
        public void CrewDialog_ANotRefusedKerbal_StaysStock()
        {
            var index = IndexOf(Dismiss(600, "Bob Kerman"));
            StockUiDecoration d = StockUiCrewDialogDecoration.Decide(
                "Bob Kerman", false, KerbalReservationKind.NotManaged, index, null, Fmt, 700);
            Assert.False(d.Blocked);
            Assert.Equal(StockUiDecorationKind.None, d.Kind);
        }

        [Fact]
        public void AstronautComplex_PastDismissal_MarksNothing()
        {
            var index = IndexOf(Dismiss(300, "Bob Kerman"));
            StockUiDecoration d = StockUiDecorationQuery.ForAstronautComplex(index, 400,
                new[] { new StockUiItem("Bob Kerman", StockUiDecorationQuery.AstronautAvailableTab) },
                new AstronautComplexContext(), Fmt).Single();
            Assert.Equal(StockUiDecorationKind.None, d.Kind);
            Assert.False(d.Marked);
            Assert.False(d.Blocked);
        }

        [Fact]
        public void AstronautComplex_FutureDismissal_OnAnApplicantRow_MarksNothing()
        {
            // A rewind to before his hire leaves him an applicant: there is no Dismiss to hold.
            var index = IndexOf(Dismiss(600, "Bob Kerman"));
            StockUiDecoration d = StockUiDecorationQuery.ForAstronautComplex(index, 400,
                new[] { new StockUiItem("Bob Kerman", StockUiDecorationQuery.AstronautApplicantsTab) },
                new AstronautComplexContext(), Fmt).Single();
            Assert.Equal(StockUiDecorationKind.None, d.Kind);
            Assert.False(d.Marked);
            Assert.False(d.Blocked);
        }

        [Fact]
        public void TimelineHover_NamesTheHeldControls()
        {
            var index = IndexOf(Dismiss(600, "Bob Kerman"));
            Assert.Equal("Dismiss and crew assignment for this kerbal",
                ReservationExplanation.TimelineHeldControl(
                    CommittedFutureKind.KerbalDismissal, "Bob Kerman", index, 590, null));
            Assert.Null(ReservationExplanation.TimelineHeldControl(
                CommittedFutureKind.KerbalDismissal, "Bob Kerman", index, 600, null));
        }

        // ---------------- hire ownership ----------------

        [Fact]
        public void Dismissal_EndsTheHireHolding_SoARehireCharges()
        {
            // Walk order: hire at 5 (captured 5th), dismissal at 10, hire at 15 captured first.
            // Without the release, the hire at 15 would read as owned by the later-captured
            // hire at 5 and charge nothing.
            var hireA = Hire(5, "Bob Kerman", 5);
            var hireB = Hire(15, "Bob Kerman", 2);
            var withDismissal = new List<GameAction> { hireA, Dismiss(10, "Bob Kerman", 6), hireB };
            var without = new List<GameAction> { hireA, hireB };

            Assert.Empty(PurchaseStateRule.CollectAlreadyOwned(withDismissal));
            Assert.Contains(hireB, PurchaseStateRule.CollectAlreadyOwned(without));
        }

        [Fact]
        public void OwnershipBook_RecordRelease_EndsOnlyTheKerbalsHire()
        {
            var book = new PurchaseOwnershipBook();
            book.RecordCharged(Hire(5, "Bob Kerman", 1));
            book.RecordCharged(Hire(5, "Val Kerman", 2));

            Assert.True(book.RecordRelease(Dismiss(10, "Bob Kerman", 3)));
            Assert.Equal(1, book.Count);
            Assert.False(book.RecordRelease(Dismiss(11, "Bob Kerman", 4)));
            Assert.False(book.RecordRelease(Hire(12, "Val Kerman", 5)));
            Assert.Equal(1, book.Count);
        }
    }
}
