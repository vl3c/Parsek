using System;
using System.Collections.Generic;
using System.Linq;
using Parsek.Patches;
using Xunit;

namespace Parsek.Tests
{
    /// <summary>
    /// The stock-screen annotation consistency pass: one wording, one date form and one
    /// colour across the D1 stock-control annotations. Owner decisions K1-a (Mission Control
    /// row "- Accepted on Y1, D03"), K2-a (stock's reason orange for every block reason, blue
    /// only for the Mission Control row status), K3-a (the VAB/SPH crew dialog's greyed row
    /// shows its status in place of the trait line), the dismissal refusals naming the
    /// flight and its date, no mod name in stock text, a crew hover that states its date once,
    /// the science-shortage refusal and its mark on the Research button, and the tech title
    /// fallback.
    /// </summary>
    [Collection("Sequential")]
    public class StockAnnotationConsistencyTests : IDisposable
    {
        private static readonly Func<double, string> Fmt = ut => "Y1, D07, 00:08";
        private static readonly Func<double, string> DateOnly = ut => "Y1, D07";

        public StockAnnotationConsistencyTests()
        {
            ParsekLog.ResetTestOverrides();
            ParsekLog.SuppressLogging = true;
            StockUiText.LocalizerForTesting = null;
        }

        public void Dispose()
        {
            StockUiText.LocalizerForTesting = null;
            TechResearchPatch.LiveScienceForTesting = null;
            LedgerOrchestrator.ResetForTesting();
            RecordingStore.ResetForTesting();
            ParsekLog.ResetTestOverrides();
        }

        // ------------------------------------------------------------ the hover title

        // catches: a crew hover saying its date twice (bold title + caption), or a dated
        // title losing its state word.
        [Theory]
        [InlineData("Reserved until Y1, D07, 00:08", "Reserved")]
        [InlineData("Lost until Y1, D09, 01:00", "Lost")]
        [InlineData("Hired on Y1, D04, 01:26", "Hired later")]
        [InlineData("Dismissed on Y1, D04, 01:26", "Dismissed later")]
        [InlineData("Reserved for Jebediah Kerman", "Reserved for Jebediah Kerman")]
        [InlineData("Stand-in for Bill Kerman", "Stand-in for Bill Kerman")]
        [InlineData("Retired", "Retired")]
        [InlineData("Reserved", "Reserved")]
        [InlineData("", "")]
        [InlineData(null, null)]
        public void UndatedTitle_DropsTheDateAndKeepsTheState(string title, string expected)
        {
            Assert.Equal(expected, ReservationExplanation.UndatedTitle(title));
            Assert.Equal(expected, StockUiAstronautDecoration.HoverTitle(title));
        }

        // catches: the Astronaut Complex hire / dismiss lock drawing the date in the bold title
        // AND in the caption (stock's "<b>title</b>\ncaption" block).
        [Fact]
        public void AstronautComplexLocks_StateTheDateOnce()
        {
            var hold = new KerbalHold
            {
                KerbalName = "Bill Kerman",
                FlightName = "Census Hopper",
                ReleaseUT = 700,
                EndState = KerbalEndState.Recovered
            };
            var text = ReservationExplanation.KerbalOnFlight(hold, Fmt);
            var held = Deco(StockUiDecorationKind.KerbalOnFlight, text.Title, text.Body);
            var dismiss = StockUiAstronautDecoration.Decide(held, "Available", "Available", true, text.Body);
            Assert.Equal("Reserved", dismiss.DisabledTitle);
            Assert.Equal("Reserved by timeline for 'Census Hopper' until Y1, D07, 00:08.", dismiss.DisabledCaption);
            Assert.Equal(1, CountOf(dismiss.DisabledTitle + "\n" + dismiss.DisabledCaption, "Y1, D07"));

            var hireText = ReservationExplanation.KerbalHire(
                new CommittedFutureEntry(CommittedFutureKind.KerbalHire, "Val", 400, null, null), Fmt);
            var hire = StockUiAstronautDecoration.Decide(
                Deco(StockUiDecorationKind.KerbalHire, hireText.Title, hireText.Body), "Applicants", "Applicant", true, null);
            Assert.Equal("Hired later", hire.DisabledTitle);
            Assert.Equal(1, CountOf(hire.DisabledTitle + "\n" + hire.DisabledCaption, "Y1, D07"));

            // The appended crew tooltip block carries the same undated title.
            string tooltip = StockUiAstronautDecoration.AppendTooltip("Pilot", StockUiAstronautDecoration.HoverTitle(text.Title), text.Body);
            Assert.Equal(1, CountOf(tooltip, "Y1, D07"));
        }

        // ------------------------------------------------------------ no mod name

        // catches: "Parsek" printed into a stock control's text.
        [Fact]
        public void StockText_NeverNamesTheMod()
        {
            Assert.Equal("Kept on the roster", StockUiAstronautDecoration.DismissBlockedTitle);
            Assert.Equal("Retired stand-in", StockUiDecorationQuery.RetiredStandInText);
            foreach (string s in new[]
            {
                StockUiAstronautDecoration.DismissBlockedTitle,
                StockUiDecorationQuery.RetiredStandInText,
                MissionControlStockAnnotation.DetailHeading,
                MissionControlStockAnnotation.CancelDetailHeading,
                MissionControlStockAnnotation.SlotDetailHeading,
                StockUiCrewDialogDecoration.FallbackTitle
            })
                Assert.DoesNotContain("Parsek", s, StringComparison.OrdinalIgnoreCase);
        }

        // ------------------------------------------------------------ dismissal refusals

        // catches: the three legacy refusals ("on timeline, blocked by timeline") coming back,
        // or a refusal dropping the flight or the date it knows.
        [Fact]
        public void DismissalRefusals_NameTheFlightAndTheDate()
        {
            var returned = new DismissalFacts
            {
                FlightName = "Kerbal X",
                FlightEndUT = 500,
                OwnerReleaseUT = double.NaN
            };
            Assert.Equal("Flown in 'Kerbal X' until Y1, D07, 00:08, blocked by timeline.",
                KerbalDismissalPatch.DescribeDismissalBlock(KerbalReservationKind.NotManaged, true, returned, Fmt));

            var retired = new DismissalFacts
            {
                ChainOwner = "Jebediah Kerman",
                FlightName = "Kerbal X",
                FlightEndUT = 500,
                OwnerReleaseUT = double.NaN
            };
            Assert.Equal("Retired after standing in for Jebediah Kerman in 'Kerbal X' until Y1, D07, 00:08, blocked by timeline.",
                KerbalDismissalPatch.DescribeDismissalBlock(KerbalReservationKind.ReservedRetired, true, retired, Fmt));

            var displaced = new DismissalFacts
            {
                ChainOwner = "Jebediah Kerman",
                OwnerFlightName = "Mun Lander",
                OwnerReleaseUT = 900,
                FlightEndUT = double.NaN
            };
            Assert.Equal("Kept as a stand-in for Jebediah Kerman, reserved by timeline for 'Mun Lander' until Y1, D07, 00:08.",
                KerbalDismissalPatch.DescribeDismissalBlock(KerbalReservationKind.NotManaged, false, displaced, Fmt));

            var active = displaced;
            active.StandInOwner = "Jebediah Kerman";
            Assert.Equal("Standing in for Jebediah Kerman, reserved by timeline for 'Mun Lander' until Y1, D07, 00:08.",
                KerbalDismissalPatch.DescribeDismissalBlock(KerbalReservationKind.NotManaged, false, active, Fmt));
        }

        // catches: an unknown part leaving a dangling "in ''" or "until UT 0" in the sentence.
        [Fact]
        public void DismissalRefusals_DropWhatIsUnknown()
        {
            Assert.Equal("Flown in 'Kerbal X', blocked by timeline.",
                ReservationExplanation.FlownDismissal("Kerbal X", double.NaN, Fmt));
            Assert.Equal("Flown in an earlier flight, blocked by timeline.",
                ReservationExplanation.FlownDismissal(null, double.NaN, Fmt));
            Assert.Equal("Retired after standing in for Jebediah Kerman, blocked by timeline.",
                ReservationExplanation.RetiredStandInDismissal("Jebediah Kerman", null, double.NaN, Fmt));
            Assert.Equal("Retired after standing in for a reserved kerbal in 'Kerbal X', blocked by timeline.",
                ReservationExplanation.RetiredStandInDismissal(null, "Kerbal X", double.NaN, Fmt));
            Assert.Equal("Kept as a stand-in for a reserved kerbal, blocked by timeline.",
                ReservationExplanation.KeptAsStandIn(null, "Mun Lander", 900, Fmt));
            Assert.Equal("Kept as a stand-in for Jebediah Kerman, reserved by timeline.",
                ReservationExplanation.KeptAsStandIn("Jebediah Kerman", null, double.PositiveInfinity, Fmt));
            Assert.Equal("Standing in for Jebediah Kerman, reserved by timeline for 'Mun Lander'.",
                ReservationExplanation.StandingIn("Jebediah Kerman", "Mun Lander"));
        }

        // catches: the live gatherer reading the wrong owner (the active-only lookup) for a
        // displaced chain member, or not reading the kerbal's own committed flight.
        [Fact]
        public void GatherDismissalFacts_ReadsTheChainOwnerAndTheKerbalsOwnLastFlight()
        {
            var module = new KerbalsModule();
            var parent = new ConfigNode("TEST");
            var slot = parent.AddNode("KERBAL_SLOTS").AddNode("SLOT");
            slot.AddValue("owner", "Jeb");
            slot.AddValue("trait", "Pilot");
            slot.AddNode("CHAIN_ENTRY").AddValue("name", "Hanley");
            slot.AddNode("CHAIN_ENTRY").AddValue("name", "Kirrim");
            module.LoadSlots(parent);
            var index = CommittedFutureIndex.Build(
                new[]
                {
                    new GameAction { UT = 10, Type = GameActionType.KerbalAssignment, KerbalName = "Kirrim", RecordingId = "r1", KerbalEndStateField = KerbalEndState.Recovered, EndUT = 100f },
                    new GameAction { UT = 20, Type = GameActionType.KerbalAssignment, KerbalName = "Kirrim", RecordingId = "r2", KerbalEndStateField = KerbalEndState.Recovered, EndUT = 300f }
                },
                id => true,
                id => id == "r1" ? "First Hop" : "Second Hop",
                null);

            Assert.Equal("Jeb", module.FindChainOwner("Kirrim"));
            Assert.Equal("Jeb", module.FindChainOwner("Hanley"));
            Assert.Null(module.FindChainOwner("Jeb"));
            Assert.Null(module.FindChainOwner("Bob"));

            var facts = KerbalDismissalPatch.GatherDismissalFacts(module, index, "Kirrim");
            Assert.Equal("Jeb", facts.ChainOwner);
            Assert.Equal("Second Hop", facts.FlightName);
            Assert.Equal(300.0, facts.FlightEndUT, 3);
            // Jeb is not reserved: no owner flight, no owner date.
            Assert.Null(facts.OwnerFlightName);
            Assert.True(double.IsNaN(facts.OwnerReleaseUT));

            var none = KerbalDismissalPatch.GatherDismissalFacts(module, null, "Kirrim");
            Assert.Null(none.FlightName);
            Assert.True(double.IsNaN(none.FlightEndUT));
        }

        // ------------------------------------------------------------ colour (K2-a)

        // catches: a block reason drawn in the informational blue, or the row status losing
        // its blue (the only non-orange Parsek colour on the stock screens).
        [Fact]
        public void BlockReasonsAreOrange_OnlyTheMissionControlRowStatusIsBlue()
        {
            Assert.Equal("#f97306", StockUiRnDDecoration.ReasonColorHex);
            Assert.Equal("#" + MissionControlStockAnnotation.DetailColor, StockUiRnDDecoration.ReasonColorHex);
            Assert.Equal("8fd3ff", MissionControlStockAnnotation.StatusColor);
            Assert.Contains(MissionControlStockAnnotation.StatusColor, MissionControlStockAnnotation.RowStatusMarker);
            Assert.DoesNotContain(MissionControlStockAnnotation.StatusColor, MissionControlStockAnnotation.DetailMarker);
            Assert.Equal("<color=#f97306>why</color>", StockUiText.ReasonColored("why"));
            Assert.Null(StockUiText.ReasonColored(null));
            Assert.Equal("", StockUiText.ReasonColored(""));

            var blocked = new StockUiDecoration
            {
                Kind = StockUiDecorationKind.ContractAccept,
                Marked = true,
                Blocked = true,
                Title = "Accepted on Y1, D03, 01:53",
                Why = "Accepted on Y1, D03, 01:53, blocked by timeline until then.",
                UT = 1
            };
            string detail = MissionControlStockAnnotation.ComposeDetailText("stock", blocked);
            Assert.DoesNotContain("8fd3ff", detail);
            Assert.Contains("<color=#f97306>" + blocked.Why + "</color>", detail);
            string row = MissionControlStockAnnotation.ComposeRowLabel("", "T", blocked, ut => "Y1, D03");
            Assert.DoesNotContain("f97306", row);
            Assert.EndsWith("<color=#8fd3ff>- Accepted on Y1, D03</color>", row);
        }

        // ------------------------------------------------------------ crew dialog (K3-a)

        // catches: the greyed row keeping its trait (no visible reason), or the row status
        // carrying the time of day the narrow row cannot fit.
        [Fact]
        public void CrewDialogTraitLine_IsTheDateOnlyStatus()
        {
            var finite = new KerbalsModule.KerbalReservation { KerbalName = "Bill Kerman", ReservedUntilUT = 700 };
            var open = new KerbalsModule.KerbalReservation { KerbalName = "Jebediah Kerman", ReservedUntilUT = double.PositiveInfinity };
            var context = new AstronautComplexContext
            {
                Reservation = n => n == "Bill Kerman" ? finite : open,
                SlotOwner = n => n
            };

            var bill = StockUiCrewDialogDecoration.Decide("Bill Kerman", true, KerbalReservationKind.ReservedActive,
                null, context, DateOnly);
            Assert.Equal("Reserved until Y1, D07", StockUiCrewDialogDecoration.TraitLineStatus(bill));

            var jeb = StockUiCrewDialogDecoration.Decide("Jebediah Kerman", true, KerbalReservationKind.ReservedActive,
                null, context, DateOnly);
            Assert.Equal("Reserved", StockUiCrewDialogDecoration.TraitLineStatus(jeb));

            var retired = StockUiCrewDialogDecoration.Decide("Lars Kerman", true, KerbalReservationKind.ReservedRetired,
                null, new AstronautComplexContext { SlotOwner = n => "Jebediah Kerman" }, DateOnly);
            Assert.Equal("Retired", StockUiCrewDialogDecoration.TraitLineStatus(retired));

            var free = StockUiCrewDialogDecoration.Decide("Bob Kerman", false, KerbalReservationKind.NotManaged,
                null, context, DateOnly);
            Assert.Null(StockUiCrewDialogDecoration.TraitLineStatus(free));

            // The hover keeps the full date, once.
            var hover = StockUiCrewDialogDecoration.Decide("Bill Kerman", true, KerbalReservationKind.ReservedActive,
                null, context, Fmt);
            Assert.Equal("Reserved", StockUiAstronautDecoration.HoverTitle(hover.Title));
            Assert.Equal("Reserved by timeline until Y1, D07, 00:08.", hover.Why);
        }

        // ------------------------------------------------------------ science shortage

        // catches: the ungrammatical "Not enough science: ... on timeline, blocked by timeline"
        // refusal coming back, or the detail line losing its numbers.
        [Fact]
        public void ScienceShortage_IsOneParticipleSentence_WithTheNumbersInTheDetail()
        {
            var text = ReservationExplanation.ScienceShortage();
            Assert.Equal("Reserved for later research, blocked by timeline.", text.Body);
            Assert.Equal("Reserved for later research", text.Title);
            Assert.Equal("Needs 45.0 science, 12.5 free.", ReservationExplanation.ScienceShortageDetail(45, 12.5));
            Assert.Equal("Needs 5.0 science, 0.0 free.", ReservationExplanation.ScienceShortageDetail(5, -3));
            Assert.Equal("Needs 5.0 science, 0.0 free.", ReservationExplanation.ScienceShortageDetail(5, double.NaN));
            Assert.Equal("Reserved for later research, blocked by timeline. Needs 45.0 science, 12.5 free.",
                StockUiRnDDecoration.ScienceShortageReason(45, 12.5));
        }

        // catches: the science-short Research button left live (a block with no mark), or
        // greyed on a researched / faded / already committed-blocked node.
        [Theory]
        [InlineData(false, false, false, true, true)]
        [InlineData(false, false, false, false, false)]
        [InlineData(true, false, false, true, false)]
        [InlineData(false, true, false, true, false)]
        [InlineData(false, false, true, true, false)]
        public void ScienceShortageMark_ReadsTheGatePredicate(
            bool committedBlocked, bool researched, bool faded, bool scienceShort, bool expected)
        {
            Assert.Equal(expected, StockUiRnDDecoration.ShouldDisableResearchForScience(
                committedBlocked, researched, faded, scienceShort));
        }

        // catches: the mark and the click gate reading different predicates (the pairing rule).
        [Fact]
        public void ScienceShortage_TheGateAndTheMarkReadOnePredicate()
        {
            string patch = TooltipEchoBudgetTests.ReadParsekSource("Patches/TechResearchPatch.cs");
            string rnd = TooltipEchoBudgetTests.ReadParsekSource("StockUiRnDDecoration.cs");
            Assert.Contains("if (IsScienceShort(sciCostCheck, out free))", patch);
            Assert.Contains("Patches.TechResearchPatch.IsScienceShort(cost, out free)", rnd);
            Assert.DoesNotContain("Not enough science", patch);
            Assert.False(TechResearchPatch.IsScienceShort(0f, out double free0));
            Assert.True(double.IsPositiveInfinity(free0));
        }

        // catches: a plain shortage (the live pool itself below the cost, nothing reserved)
        // greyed and refused as a timeline block, or a timeline shortage left unmarked.
        [Theory]
        [InlineData(45.0, 12.0, 12.0, false)]   // plain: 12 science, a 45-science node
        [InlineData(45.0, 50.0, 12.0, true)]    // timeline: the pool covers it, the free science does not
        [InlineData(45.0, 45.0, 44.9, true)]    // boundary: live exactly the cost
        [InlineData(45.0, 44.9, 0.0, false)]    // boundary: live just under the cost
        [InlineData(45.0, 50.0, 45.0, false)]   // boundary: free exactly the cost
        [InlineData(45.0, double.NaN, 0.0, false)] // no R&D singleton
        [InlineData(0.0, 50.0, 0.0, false)]     // a free node
        public void TimelineScienceShortage_IsOnlyALivePoolTheReservationEats(
            double cost, double live, double free, bool expected)
        {
            Assert.Equal(expected, TechResearchPatch.IsTimelineScienceShortage(cost, free, live));
        }

        // catches: the gate probing (and refusing) when the live pool is already short:
        // stock's own refusal decides, with no Parsek grey, tooltip or dialog.
        [Fact]
        public void IsScienceShort_APlainShortageIsLeftToStock_WithoutAProbe()
        {
            TechResearchPatch.LiveScienceForTesting = () => 12.0;
            double free;
            Assert.False(TechResearchPatch.IsScienceShort(45f, out free));
            Assert.True(double.IsPositiveInfinity(free), "a plain shortage should not run the probe");

            TechResearchPatch.LiveScienceForTesting = () => double.NaN;
            Assert.False(TechResearchPatch.IsScienceShort(45f, out free));
        }

        // catches: the probe rewriting the action-derived fields of the shared ledger rows
        // (the Timeline reads them), or leaving the live science module walked to the cutoff.
        [Fact]
        public void AffordabilityProbe_RestoresDerivedFields_AndLeavesTheLiveModule()
        {
            LedgerOrchestrator.ResetForTesting();
            RecordingStore.ResetForTesting();
            LedgerOrchestrator.Initialize();
            var seed = new GameAction { UT = 0, Type = GameActionType.ScienceInitial, InitialScience = 100f };
            var spend = new GameAction { UT = 500, Type = GameActionType.ScienceSpending, NodeId = "basicRocketry", Cost = 30f };
            Ledger.AddAction(seed);
            Ledger.AddAction(spend);
            LedgerOrchestrator.RecalculateAndPatch();
            double runningBefore = LedgerOrchestrator.Science.GetRunningScience();
            spend.Effective = false;
            spend.NotCountedReason = GameActionNotCountedReason.None;
            spend.Affordable = true;

            LedgerOrchestrator.NowUtProviderForTesting = () => 100.0;
            double free;
            Assert.True(LedgerOrchestrator.CanAffordScienceSpending(60f, out free));
            Assert.Equal(70.0, free, 3);
            Assert.False(LedgerOrchestrator.CanAffordScienceSpending(80f));

            Assert.False(spend.Effective);
            Assert.True(spend.Affordable);
            Assert.Equal(runningBefore, LedgerOrchestrator.Science.GetRunningScience(), 6);
        }

        // ------------------------------------------------------------ tech title

        // catches: an empty RDTech.title printing the internal tech id while the tree has a
        // title, or a localization key printed raw.
        [Fact]
        public void TechDisplayTitle_ResolvesTheStockTitle_BeforeFallingBackToTheId()
        {
            StockUiText.LocalizerForTesting = key => key == "#autoLOC_501020" ? "Basic Rocketry" : key;
            Func<string, string> tree = id => id == "basicRocketry" ? "#autoLOC_501020" : "";

            Assert.Equal("Basic Rocketry", TechResearchPatch.DisplayTitle("Basic Rocketry", "basicRocketry", tree));
            Assert.Equal("Basic Rocketry", TechResearchPatch.DisplayTitle("#autoLOC_501020", "basicRocketry", tree));
            Assert.Equal("Basic Rocketry", TechResearchPatch.DisplayTitle("", "basicRocketry", tree));
            Assert.Equal("Basic Rocketry", TechResearchPatch.DisplayTitle(null, "basicRocketry", tree));
            Assert.Equal("unknownNode", TechResearchPatch.DisplayTitle("", "unknownNode", tree));
            Assert.Equal("unknownNode", TechResearchPatch.DisplayTitle(null, "unknownNode", null));
            Assert.Equal("unknownNode", TechResearchPatch.DisplayTitle(null, "unknownNode", id => throw new InvalidOperationException()));
        }

        // ------------------------------------------------------------ the wording rule

        // catches: a new sentence in this pass breaking the owner's wording rule.
        [Fact]
        public void EveryNewSentence_FollowsTheWordingRule()
        {
            var texts = new List<string>
            {
                ReservationExplanation.FlownDismissal("Kerbal X", 500, Fmt),
                ReservationExplanation.FlownDismissal(null, double.NaN, Fmt),
                ReservationExplanation.RetiredStandInDismissal("J", "Kerbal X", 500, Fmt),
                ReservationExplanation.RetiredStandInDismissal(null, null, double.NaN, Fmt),
                ReservationExplanation.KeptAsStandIn("J", "Mun Lander", 900, Fmt),
                ReservationExplanation.KeptAsStandIn(null, null, double.NaN, Fmt),
                ReservationExplanation.StandingIn("J", "Mun Lander", 900, Fmt),
                ReservationExplanation.ScienceShortage().Body,
                StockUiRnDDecoration.ScienceShortageReason(45, 12),
                StockUiAstronautDecoration.DismissBlockedTitle,
                StockUiDecorationQuery.RetiredStandInText,
                MissionControlStockAnnotation.RowStatus(new StockUiDecoration
                {
                    Kind = StockUiDecorationKind.ContractResolution, Title = "Cancelled on x", UT = 1
                }, ut => "Y12, D426")
            };
            foreach (string t in texts)
            {
                Assert.False(string.IsNullOrEmpty(t));
                Assert.True(t.All(c => c < 128), "non-ASCII in: " + t);
                Assert.DoesNotContain("your timeline", t, StringComparison.OrdinalIgnoreCase);
                Assert.DoesNotContain("the timeline", t, StringComparison.OrdinalIgnoreCase);
                Assert.DoesNotContain("committed", t, StringComparison.OrdinalIgnoreCase);
                Assert.DoesNotContain("on timeline, blocked by timeline", t);
                Assert.DoesNotContain("Parsek", t);
            }
        }

        private static StockUiDecoration Deco(StockUiDecorationKind kind, string title, string why)
        {
            return new StockUiDecoration
            {
                Kind = kind,
                Marked = kind != StockUiDecorationKind.None,
                Blocked = true,
                Title = title,
                Why = why,
                UT = double.NaN
            };
        }

        private static int CountOf(string text, string needle)
        {
            int n = 0, at = 0;
            while ((at = text.IndexOf(needle, at, StringComparison.Ordinal)) >= 0)
            {
                n++;
                at += needle.Length;
            }
            return n;
        }
    }
}
