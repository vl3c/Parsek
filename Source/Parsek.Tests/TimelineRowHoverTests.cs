using System;
using System.Collections.Generic;
using System.Globalization;
using Xunit;

namespace Parsek.Tests
{
    /// <summary>
    /// The Timeline row hover: the pure per-kind builders (TimelineRowHover), the hold
    /// sentence (ReservationExplanation.ForTimelineRow) against the click-block predicates,
    /// the walk's not-counted reason stamping, the builder's contract pairing, the hovered-row
    /// memo, and the "timeline" wording rule over the Parsek-window texts it touched.
    /// </summary>
    [Collection("Sequential")]
    public class TimelineRowHoverTests : IDisposable
    {
        private readonly List<string> logLines = new List<string>();

        // "D<day>" from whole hundreds, like KSPUtil.PrintDateCompact reads.
        private static readonly Func<double, string> Fmt =
            ut => "D" + ((long)(ut / 100.0)).ToString(CultureInfo.InvariantCulture);

        public TimelineRowHoverTests()
        {
            ParsekLog.ResetTestOverrides();
            ParsekLog.TestSinkForTesting = line => logLines.Add(line);
            RecalculationEngine.ClearModules();
        }

        public void Dispose()
        {
            RecalculationEngine.ClearModules();
            ParsekLog.ResetTestOverrides();
            ParsekLog.SuppressLogging = true;
        }

        private static CommittedFutureIndex Index(params GameAction[] actions)
        {
            return CommittedFutureIndex.Build(actions, id => true, id => null, null);
        }

        private static TimelineEntry Row(GameAction a)
        {
            return new TimelineEntry { UT = a.UT, Action = a, Source = TimelineSource.GameAction };
        }

        // ---------------- the hold sentence ----------------

        // catches: a future row naming a stock hold, or the wrong control, for any kind the
        // click-blocks key on.
        [Fact]
        public void ForTimelineRow_NamesTheControlEachFutureRowHolds()
        {
            var tech = new GameAction { UT = 600, Type = GameActionType.ScienceSpending, NodeId = "basicRocketry" };
            var accept = new GameAction { UT = 600, Type = GameActionType.ContractAccept, ContractId = "c1" };
            var complete = new GameAction { UT = 700, Type = GameActionType.ContractComplete, ContractId = "c1" };
            var upgrade = new GameAction { UT = 600, Type = GameActionType.FacilityUpgrade, FacilityId = "SpaceCenter/LaunchPad", ToLevel = 2 };
            var hire = new GameAction { UT = 600, Type = GameActionType.KerbalHire, KerbalName = "Val" };
            var activate = new GameAction { UT = 600, Type = GameActionType.StrategyActivate, StrategyId = "S1" };
            var deactivate = new GameAction { UT = 600, Type = GameActionType.StrategyDeactivate, StrategyId = "S2" };
            var part = new GameAction { UT = 600, Type = GameActionType.FundsSpending, FundsSpendingSource = FundsSpendingSource.Other, DedupKey = "mk1pod.v2", FundsSpent = 100f };
            var index = Index(tech, accept, complete, upgrade, hire, activate, deactivate, part);

            Assert.Equal("Holds Research in R&D until D6.", ReservationExplanation.ForTimelineRow(Row(tech), index, 500, Fmt));
            Assert.Equal("Holds Accept and Decline in Mission Control until D6.",
                ReservationExplanation.ForTimelineRow(Row(accept), index, 500, Fmt));
            Assert.Equal("Holds Cancel in Mission Control until D7.",
                ReservationExplanation.ForTimelineRow(Row(complete), index, 500, Fmt));
            Assert.Equal("Holds Upgrade and Rebuild on this facility until D6.", ReservationExplanation.ForTimelineRow(Row(upgrade), index, 500, Fmt));
            Assert.Equal("Holds Hire in the Astronaut Complex until D6.", ReservationExplanation.ForTimelineRow(Row(hire), index, 500, Fmt));
            Assert.Equal("Holds Activate in Administration until D6.",
                ReservationExplanation.ForTimelineRow(Row(activate), index, 500, Fmt));
            Assert.Equal("Holds Deactivate in Administration until D6.",
                ReservationExplanation.ForTimelineRow(Row(deactivate), index, 500, Fmt));
            // A part purchase reads the live decision; without it nothing is claimed.
            Assert.Null(ReservationExplanation.ForTimelineRow(Row(part), index, 500, Fmt));
            Assert.Null(ReservationExplanation.ForTimelineRow(Row(part), index, 500, Fmt, p => false));
            Assert.Equal("Holds Purchase for this part until D6.",
                ReservationExplanation.ForTimelineRow(Row(part), index, 500, Fmt, p => p == "mk1pod.v2"));
        }

        // catches: a strategy hover naming a button stock does not show: an inactive
        // strategy offers only Activate, an active one only Deactivate.
        [Fact]
        public void ForTimelineRow_StrategyNamesOnlyTheButtonStockShowsNow()
        {
            var activate = new GameAction { UT = 600, Type = GameActionType.StrategyActivate, StrategyId = "S1" };
            var deactivate = new GameAction { UT = 700, Type = GameActionType.StrategyDeactivate, StrategyId = "S2" };
            var index = Index(activate, deactivate);
            Func<string, bool> none = id => false;
            Func<string, bool> all = id => true;

            Assert.Equal("Holds Activate in Administration until D6.",
                ReservationExplanation.ForTimelineRow(Row(activate), index, 500, Fmt, null, none));
            // Active now (e.g. a re-activation after a committed deactivation): Deactivate,
            // which IsDeactivationBlocked refuses while the row is ahead.
            Assert.True(StrategyReservationPredicates.IsDeactivationBlocked(index, "S1", 500));
            Assert.Equal("Holds Deactivate in Administration until D6.",
                ReservationExplanation.ForTimelineRow(Row(activate), index, 500, Fmt, null, all));
            Assert.DoesNotContain("Activate and Deactivate",
                ReservationExplanation.ForTimelineRow(Row(activate), index, 500, Fmt, null, all));

            Assert.Equal("Holds Deactivate in Administration until D7.",
                ReservationExplanation.ForTimelineRow(Row(deactivate), index, 500, Fmt, null, all));
            Assert.Null(ReservationExplanation.ForTimelineRow(Row(deactivate), index, 500, Fmt, null, none));
            // Unknown state: the row's own button.
            Assert.Equal("Holds Deactivate in Administration until D7.",
                ReservationExplanation.ForTimelineRow(Row(deactivate), index, 500, Fmt));
        }

        // catches: the hover claiming a hold the click-block predicate does not enforce
        // (the pairing rule, applied to the Timeline): the answer must flip exactly where
        // the predicate flips.
        [Fact]
        public void ForTimelineRow_AgreesWithTheClickBlockPredicate()
        {
            var accept = new GameAction { UT = 600, Type = GameActionType.ContractAccept, ContractId = "c1" };
            var tech = new GameAction { UT = 600, Type = GameActionType.ScienceSpending, NodeId = "n1" };
            var index = Index(accept, tech);
            foreach (double now in new[] { 0.0, 599.0, 600.0, 601.0, 5000.0 })
            {
                Assert.Equal(StockUiReservationPredicates.IsContractAcceptBlocked(index, "c1", now),
                    ReservationExplanation.ForTimelineRow(Row(accept), index, now, Fmt) != null);
                Assert.Equal(StockUiReservationPredicates.IsTechResearchBlocked(index, "n1", now),
                    ReservationExplanation.ForTimelineRow(Row(tech), index, now, Fmt) != null);
            }
            // A row outside the index (an uncommitted recording's row) holds nothing.
            var tagged = new GameAction { UT = 600, Type = GameActionType.ContractAccept, ContractId = "c1", RecordingId = "live" };
            Assert.Null(ReservationExplanation.ForTimelineRow(Row(tagged),
                CommittedFutureIndex.Build(new[] { tagged }, id => false, id => null, null), 500, Fmt));
        }

        // catches: a past row, a repair row (live building state unknown), a milestone or
        // a recording row claiming a hold.
        [Fact]
        public void ForTimelineRow_NullForPastRowsAndKindsThatHoldNothing()
        {
            var tech = new GameAction { UT = 600, Type = GameActionType.ScienceSpending, NodeId = "n1" };
            var repair = new GameAction { UT = 600, Type = GameActionType.FacilityRepair, FacilityId = "SpaceCenter/Runway/Facility/a" };
            var milestone = new GameAction { UT = 600, Type = GameActionType.MilestoneAchievement, MilestoneId = "FirstLaunch" };
            var index = Index(tech, repair, milestone);
            Assert.Null(ReservationExplanation.ForTimelineRow(Row(tech), index, 600, Fmt));
            Assert.Null(ReservationExplanation.ForTimelineRow(Row(repair), index, 500, Fmt));
            Assert.Null(ReservationExplanation.ForTimelineRow(Row(milestone), index, 500, Fmt));
            Assert.Null(ReservationExplanation.ForTimelineRow(new TimelineEntry { UT = 600 }, index, 500, Fmt));
            Assert.Null(ReservationExplanation.ForTimelineRow(Row(tech), null, 500, Fmt));
        }

        // ---------------- not counted ----------------

        [Fact]
        public void NotCounted_EveryReasonHasOneSentence_NoneHasNone()
        {
            Assert.Null(TimelineRowHover.NotCounted(GameActionNotCountedReason.None));
            Assert.Equal("Not counted: already completed earlier on timeline, so no reward was paid.",
                TimelineRowHover.NotCounted(GameActionNotCountedReason.ContractAlreadyCompleted));
            foreach (GameActionNotCountedReason r in Enum.GetValues(typeof(GameActionNotCountedReason)))
            {
                if (r == GameActionNotCountedReason.None) continue;
                string s = TimelineRowHover.NotCounted(r);
                Assert.StartsWith("Not counted: ", s);
                Assert.EndsWith(".", s);
            }
        }

        // catches: the walk leaving a greyed row with no reason, or a stale reason surviving
        // into a walk where the row counts.
        [Fact]
        public void Walk_StampsTheReason_AndTheNextWalkResetsIt()
        {
            var contracts = new ContractsModule();
            RecalculationEngine.RegisterModule(contracts, RecalculationEngine.ModuleTier.FirstTier);
            var accept = new GameAction { UT = 100, Type = GameActionType.ContractAccept, ContractId = "c1" };
            var first = new GameAction { UT = 200, Type = GameActionType.ContractComplete, ContractId = "c1", FundsReward = 10f };
            var dup = new GameAction { UT = 300, Type = GameActionType.ContractComplete, ContractId = "c1", FundsReward = 10f };
            var actions = new List<GameAction> { accept, first, dup };

            RecalculationEngine.Recalculate(actions);
            Assert.True(first.Effective);
            Assert.Equal(GameActionNotCountedReason.None, first.NotCountedReason);
            Assert.False(dup.Effective);
            Assert.Equal(GameActionNotCountedReason.ContractAlreadyCompleted, dup.NotCountedReason);

            // Remove the first completion: the former duplicate now counts, reason cleared.
            actions.Remove(first);
            RecalculationEngine.Recalculate(actions);
            Assert.True(dup.Effective);
            Assert.Equal(GameActionNotCountedReason.None, dup.NotCountedReason);
        }

        [Fact]
        public void ContractsModule_StampsResolvedAndOutcomeAfterEnd()
        {
            var m = new ContractsModule();
            m.ProcessAction(new GameAction { UT = 100, Type = GameActionType.ContractAccept, ContractId = "c1" });
            m.ProcessAction(new GameAction { UT = 150, Type = GameActionType.ContractCancel, ContractId = "c1", FundsPenalty = 5f });
            var lateComplete = new GameAction { UT = 200, Type = GameActionType.ContractComplete, ContractId = "c1" };
            m.ProcessAction(lateComplete);
            Assert.False(lateComplete.Effective);
            Assert.Equal(GameActionNotCountedReason.ContractAlreadyResolved, lateComplete.NotCountedReason);

            var m2 = new ContractsModule();
            m2.ProcessAction(new GameAction { UT = 100, Type = GameActionType.ContractAccept, ContractId = "c2" });
            m2.ProcessAction(new GameAction { UT = 200, Type = GameActionType.ContractComplete, ContractId = "c2" });
            var lateFail = new GameAction { UT = 300, Type = GameActionType.ContractFail, ContractId = "c2", FundsPenalty = 5f };
            m2.ProcessAction(lateFail);
            Assert.False(lateFail.Effective);
            Assert.Equal(GameActionNotCountedReason.ContractOutcomeAfterEnd, lateFail.NotCountedReason);
        }

        [Fact]
        public void MilestonesModule_StampsAlreadyAchieved()
        {
            var m = new MilestonesModule();
            var a = new GameAction { UT = 100, Type = GameActionType.MilestoneAchievement, MilestoneId = "FirstLaunch" };
            var b = new GameAction { UT = 200, Type = GameActionType.MilestoneAchievement, MilestoneId = "FirstLaunch" };
            m.ProcessAction(a);
            m.ProcessAction(b);
            Assert.True(a.Effective);
            Assert.False(b.Effective);
            Assert.Equal(GameActionNotCountedReason.MilestoneAlreadyAchieved, b.NotCountedReason);
        }

        // ---------------- contract rows ----------------

        [Fact]
        public void ContractAccept_TermsFromTheAcceptAndItsCompletion()
        {
            var accept = new GameAction
            {
                UT = 100, Type = GameActionType.ContractAccept, ContractId = "c1",
                DeadlineUT = 10500, AdvanceFunds = 12000f
            };
            var complete = new GameAction
            {
                UT = 900, Type = GameActionType.ContractComplete, ContractId = "c1",
                FundsReward = 39270f, RepReward = 18f
            };
            Assert.Equal("Deadline D105. Advance 12000, reward 39270 funds + 18 rep. Agent: C7 Aerospace.",
                TimelineRowHover.ContractAccept(accept, complete, "C7 Aerospace", Fmt));
            // No completion yet: the advance alone, and no agent when none is known.
            Assert.Equal("Deadline D105. Advance 12000 funds.",
                TimelineRowHover.ContractAccept(accept, null, null, Fmt));
            // No deadline, no advance: the reward alone.
            var bare = new GameAction { UT = 100, Type = GameActionType.ContractAccept, ContractId = "c1" };
            Assert.Equal("Reward 39270 funds + 18 rep.", TimelineRowHover.ContractAccept(bare, complete, null, Fmt));
            Assert.Null(TimelineRowHover.ContractAccept(bare, null, null, Fmt));
        }

        [Fact]
        public void ContractOutcome_FlightThenWhatTheRowDoesNotShow()
        {
            var complete = new GameAction
            {
                Type = GameActionType.ContractComplete, FundsReward = 100f, RepReward = 18f, ScienceReward = 12f
            };
            Assert.Equal("By flight 'Mun Lander'. Also +18 rep, +12 sci.",
                TimelineRowHover.ContractOutcome(complete, "Mun Lander"));
            var fail = new GameAction { Type = GameActionType.ContractFail, FundsPenalty = 5000f, RepPenalty = 10f };
            Assert.Equal("Penalty 5000 funds + 10 rep.", TimelineRowHover.ContractOutcome(fail, null));
            Assert.Null(TimelineRowHover.ContractOutcome(
                new GameAction { Type = GameActionType.ContractComplete, FundsReward = 100f }, null));
        }

        // catches: the accept row naming a duplicate (zeroed) completion's rewards, or an
        // earlier lifecycle's completion.
        [Fact]
        public void Builder_PairsTheAcceptWithItsFirstCountedCompletion()
        {
            var early = new GameAction { UT = 50, Type = GameActionType.ContractComplete, ContractId = "c1", FundsReward = 1f };
            var accept = new GameAction { UT = 100, Type = GameActionType.ContractAccept, ContractId = "c1" };
            var zeroed = new GameAction { UT = 200, Type = GameActionType.ContractComplete, ContractId = "c1", FundsReward = 2f, Effective = false };
            var counted = new GameAction { UT = 300, Type = GameActionType.ContractComplete, ContractId = "c1", FundsReward = 3f };
            var index = TimelineBuilder.BuildContractCompleteIndex(new[] { counted, zeroed, accept, early });
            Assert.Same(counted, TimelineBuilder.FindPairedContractComplete(index, accept));

            var entries = TimelineBuilder.Build(
                new List<Recording>(), new List<GameAction> { accept, counted }, new List<Milestone>(), null);
            TimelineEntry acceptRow = entries.Find(e => e.Type == TimelineEntryType.ContractAccept);
            Assert.Same(accept, acceptRow.Action);
            Assert.Same(counted, acceptRow.PairedContractComplete);
            Assert.Null(entries.Find(e => e.Type == TimelineEntryType.ContractComplete).PairedContractComplete);
        }

        // ---------------- launch rows ----------------

        [Fact]
        public void Launch_CrewEndAndMission()
        {
            var crew = new List<string> { "Jebediah Kerman", "Bill Kerman", "Bob Kerman", "Valentina Kerman", "Bob Kerman" };
            Assert.Equal("Jebediah, Bill, Bob +1", TimelineRowHover.CrewList(crew));
            var end = new Recording
            {
                TerminalStateValue = TerminalState.Landed,
                TerminalPosition = new SurfacePosition { body = "Mun" },
                EndBiome = "Midlands"
            };
            Assert.Equal("Ends landed at Midlands on Mun.", TimelineRowHover.LaunchEnd(end));
            Assert.Equal("Crew: Jeb, Bill. Ends landed at Midlands on Mun. Mission: Mun Landing.",
                TimelineRowHover.Launch(new[] { "Jeb", "Bill" }, TimelineRowHover.LaunchEnd(end), "Mun Landing"));
            Assert.Null(TimelineRowHover.Launch(null, null, null));
            Assert.Null(TimelineRowHover.LaunchEnd(new Recording()));
            Assert.Equal("Ends orbiting Kerbin.", TimelineRowHover.LaunchEnd(
                new Recording { TerminalStateValue = TerminalState.Orbiting, TerminalOrbitBody = "Kerbin" }));
            Assert.Equal("Ends recovered.", TimelineRowHover.LaunchEnd(
                new Recording { TerminalStateValue = TerminalState.Recovered }));
        }

        // catches: the launch hover reading the root's own (branch-point) end instead of
        // where the launched vessel's line ends, or a debris / other-launch recording's end.
        [Fact]
        public void ResolveLaunchEnd_FollowsTheSameLaunchAcrossChainAndTree()
        {
            var root = new Recording { RecordingId = "r", TreeId = "t", VesselPersistentId = 7, RecordedVesselGuid = "g1", ExplicitEndUT = 100 };
            var tip = new Recording { RecordingId = "tip", TreeId = "t", VesselPersistentId = 7, RecordedVesselGuid = "g1", ExplicitEndUT = 500, TerminalStateValue = TerminalState.Landed };
            var debris = new Recording { RecordingId = "d", TreeId = "t", VesselPersistentId = 7, IsDebris = true, ExplicitEndUT = 900, TerminalStateValue = TerminalState.Destroyed };
            var otherLaunch = new Recording { RecordingId = "o", TreeId = "t", VesselPersistentId = 7, RecordedVesselGuid = "g2", ExplicitEndUT = 800, TerminalStateValue = TerminalState.Orbiting };
            var sibling = new Recording { RecordingId = "s", TreeId = "t", VesselPersistentId = 9, ExplicitEndUT = 950, TerminalStateValue = TerminalState.Splashed };
            var all = new List<Recording> { root, tip, debris, otherLaunch, sibling };
            Assert.Same(tip, TimelineRowHover.ResolveLaunchEnd(root, all));

            var c0 = new Recording { RecordingId = "c0", ChainId = "ch", ExplicitEndUT = 100 };
            var c1 = new Recording { RecordingId = "c1", ChainId = "ch", ExplicitEndUT = 300, TerminalStateValue = TerminalState.Orbiting };
            Assert.Same(c1, TimelineRowHover.ResolveLaunchEnd(c0, new List<Recording> { c0, c1 }));
            Assert.Same(c0, TimelineRowHover.ResolveLaunchEnd(c0, null));
        }

        // ---------------- the hovered-row memo ----------------

        // catches: hover text built for every row every frame, a stale row keeping its
        // tooltip after the mouse left, a Layout pass moving the hovered row, or a stale
        // hold after the row crossed now.
        [Fact]
        public void Tracker_OnlyTheRowHoveredOnTheLastRepaint_BuildsOncePerEntry()
        {
            var t = new TimelineRowHoverTracker();
            var e1 = new TimelineEntry { UT = 1 };
            var e2 = new TimelineEntry { UT = 2 };
            int builds = 0;
            Func<TimelineEntry, bool, string> build = (e, f) => { builds++; return "hover " + e.UT.ToString(CultureInfo.InvariantCulture) + (f ? " future" : ""); };

            // Nothing hovered before the first Repaint.
            Assert.False(t.WantsTooltip(0));

            // Layout pass: observations are ignored.
            t.BeginList(isRepaint: false);
            t.ObserveRow(1, true);
            t.EndList();
            Assert.Equal(-1, t.HoveredRow);

            // Repaint pass: row 1 held the mouse.
            t.BeginList(isRepaint: true);
            t.ObserveRow(0, false);
            t.ObserveRow(1, true);
            t.EndList();
            Assert.Equal(1, t.HoveredRow);
            Assert.False(t.WantsTooltip(0));
            Assert.True(t.WantsTooltip(1));

            Assert.Equal("hover 2 future", t.GetText(e2, true, build));
            Assert.Equal("hover 2 future", t.GetText(e2, true, build));
            Assert.Equal(1, builds);
            // The row crossed now: rebuilt once.
            Assert.Equal("hover 2", t.GetText(e2, false, build));
            Assert.Equal(2, builds);
            t.GetText(e1, false, build);
            Assert.Equal(3, builds);
            Assert.Equal(2, t.MemoCount);

            // The mouse left every row: no tooltip next frame.
            t.BeginList(isRepaint: true);
            t.ObserveRow(0, false);
            t.ObserveRow(1, false);
            t.EndList();
            Assert.Equal(-1, t.HoveredRow);

            // A rebuild drops the memo.
            t.Reset();
            Assert.Equal(0, t.MemoCount);
            t.GetText(e2, false, build);
            Assert.Equal(4, builds);
        }

        [Fact]
        public void Compose_SkipsEmptyParts()
        {
            Assert.Equal("A. B.", TimelineRowHover.Compose(null, "A.", "", "B."));
            Assert.Null(TimelineRowHover.Compose(null, ""));
        }

        // ---------------- the wording rule ----------------

        /// <summary>
        /// Every Timeline hover text and every Parsek-window text this change reworded:
        /// plain ASCII, and never "your timeline", "the timeline" or "committed".
        /// </summary>
        [Fact]
        public void ParsekWindowTexts_NeverSayYourTimeline_TheTimeline_OrCommitted()
        {
            var texts = new List<string>
            {
                TimelineWindowUI.NowDividerTooltip,
                TimelineRowHover.ContractAccept(new GameAction { DeadlineUT = 900, AdvanceFunds = 5f },
                    new GameAction { FundsReward = 1f, RepReward = 1f, ScienceReward = 1f }, "Agent", Fmt),
                TimelineRowHover.ContractOutcome(new GameAction { Type = GameActionType.ContractComplete, RepReward = 1f }, "F"),
                TimelineRowHover.Launch(new[] { "Jebediah Kerman" }, "Ends recovered.", "M"),
                CrewReservationManager.FormatReservedCrewSwapMessage(new[] { new KeyValuePair<string, string>("J", "D") }),
                CrewReservationManager.FormatReservedCrewSwapMessage(new[]
                {
                    new KeyValuePair<string, string>("J", "D"), new KeyValuePair<string, string>("B", "E")
                }),
                LogisticsDormantPresentation.BuildDeleteDormantConfirmBody("Route"),
                MergeDialog.BuildReFlyDialogBody("Vessel", 60, ReFlyAutoSealPreviewResult.NoSeal(), false),
                KerbalsPresentation.ReservationHoldRule,
                KerbalsPresentation.FormatStatusTooltip(KerbalsPresentation.RosterStatus.Lost, "J", "J", null, null, null, false),
                KerbalsPresentation.FormatStatusTooltip(KerbalsPresentation.RosterStatus.Reserved, "J", "J", null, null, null, false),
                KerbalsPresentation.FormatStatusTooltip(KerbalsPresentation.RosterStatus.Reserved, "L", "J", null, null, null, false),
            };
            foreach (GameActionNotCountedReason r in Enum.GetValues(typeof(GameActionNotCountedReason)))
                if (r != GameActionNotCountedReason.None) texts.Add(TimelineRowHover.NotCounted(r));
            var index = Index(
                new GameAction { UT = 600, Type = GameActionType.ScienceSpending, NodeId = "n" },
                new GameAction { UT = 600, Type = GameActionType.ContractAccept, ContractId = "c" },
                new GameAction { UT = 600, Type = GameActionType.StrategyActivate, StrategyId = "s" });
            texts.Add(ReservationExplanation.ForTimelineRow(Row(new GameAction { UT = 600, Type = GameActionType.ScienceSpending, NodeId = "n" }), index, 0, Fmt));
            texts.Add(ReservationExplanation.ForTimelineRow(Row(new GameAction { UT = 600, Type = GameActionType.ContractAccept, ContractId = "c" }), index, 0, Fmt));
            texts.Add(ReservationExplanation.ForTimelineRow(Row(new GameAction { UT = 600, Type = GameActionType.StrategyActivate, StrategyId = "s" }), index, 0, Fmt));

            foreach (string text in texts)
            {
                Assert.False(string.IsNullOrEmpty(text));
                foreach (char c in text)
                    Assert.True(c < 128 || c == '—', "non-ASCII char in: " + text);
                Assert.DoesNotContain("your timeline", text, StringComparison.OrdinalIgnoreCase);
                Assert.DoesNotContain("the timeline", text, StringComparison.OrdinalIgnoreCase);
                Assert.DoesNotContain("committed", text, StringComparison.OrdinalIgnoreCase);
            }
        }
    }
}
