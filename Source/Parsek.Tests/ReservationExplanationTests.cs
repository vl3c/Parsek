using System;
using System.Collections.Generic;
using System.Globalization;
using Parsek.Patches;
using Xunit;

namespace Parsek.Tests
{
    /// <summary>
    /// The explanation texts (ReservationExplanation.cs) in the owner's 2026-09-27 wording:
    /// the participle, the exact date and time, then "blocked by timeline until then." (a
    /// kerbal: "Reserved by timeline for 'Flight' until DATE."), for every kind and variant,
    /// with a deterministic date formatter.
    /// </summary>
    public class ReservationExplanationTests
    {
        // "D<day>" from whole days, so the tests read like KSPUtil.PrintDateCompact.
        private static readonly Func<double, string> Fmt =
            ut => "D" + ((long)(ut / 100.0)).ToString(CultureInfo.InvariantCulture);

        private static CommittedFutureEntry Entry(CommittedFutureKind kind, double ut, string flight = null, int level = 0)
        {
            return new CommittedFutureEntry(kind, "k", ut, flight != null ? "rec" : null, flight, facilityToLevel: level);
        }

        [Fact]
        public void Tech_ParticipleDateAndBlockedUntilThen()
        {
            var text = ReservationExplanation.Tech(Entry(CommittedFutureKind.TechResearch, 11400, "Mun Lander 3"), Fmt);
            Assert.Equal("Researched on D114", text.Title);
            Assert.Equal("Researched on D114, blocked by timeline until then.", text.Body);
            // A flight name or none reads the same.
            Assert.Equal(text.Body, ReservationExplanation.Tech(Entry(CommittedFutureKind.TechResearch, 11400), Fmt).Body);
        }

        [Fact]
        public void PartPurchase_And_ContractAccept_And_Hire_And_Dismissal()
        {
            Assert.Equal("Purchased on D114, blocked by timeline until then.",
                ReservationExplanation.PartPurchase(Entry(CommittedFutureKind.PartPurchase, 11400), Fmt).Body);
            var accept = ReservationExplanation.ContractAccept(Entry(CommittedFutureKind.ContractAccept, 11400, "Mun Lander 3"), Fmt);
            Assert.Equal("Accepted on D114", accept.Title);
            Assert.Equal("Accepted on D114, blocked by timeline until then.", accept.Body);
            var hire = ReservationExplanation.KerbalHire(Entry(CommittedFutureKind.KerbalHire, 11400), Fmt);
            Assert.Equal("Hired on D114", hire.Title);
            Assert.Equal("Hired on D114, blocked by timeline until then.", hire.Body);
            var retire = ReservationExplanation.KerbalRetire(Entry(CommittedFutureKind.KerbalRetire, 11400), Fmt);
            Assert.Equal("Dismissed on D114", retire.Title);
            Assert.Equal("Dismissed on D114, blocked by timeline until then.", retire.Body);
        }

        [Fact]
        public void ContractResolution_PerOutcome()
        {
            Assert.Equal("Completed on D114, blocked by timeline until then.",
                ReservationExplanation.ContractResolution(Entry(CommittedFutureKind.ContractComplete, 11400, "X"), Fmt).Body);
            Assert.Equal("Failed on D114, blocked by timeline until then.",
                ReservationExplanation.ContractResolution(Entry(CommittedFutureKind.ContractFail, 11400), Fmt).Body);
            Assert.Equal("Expired on D114, blocked by timeline until then.",
                ReservationExplanation.ContractResolution(new CommittedFutureEntry(
                    CommittedFutureKind.ContractFail, "k", 11400, null, null, deadlineExpiry: true), Fmt).Body);
            Assert.Equal("Cancelled on D114, blocked by timeline until then.",
                ReservationExplanation.ContractResolution(Entry(CommittedFutureKind.ContractCancel, 11400), Fmt).Body);
            Assert.Equal("Completed on D114",
                ReservationExplanation.ContractResolution(Entry(CommittedFutureKind.ContractComplete, 11400), Fmt).Title);
        }

        [Fact]
        public void ContractSlot_NamesTheContractAndAgent()
        {
            var e = new CommittedFutureEntry(CommittedFutureKind.ContractAccept, "k", 11400, "rec", "Mun Lander",
                title: "Survey Kerbin", agentTitle: "Zaltonic Electronics");
            var text = ReservationExplanation.ContractSlot(e, Fmt);
            Assert.Equal("Slot needed from D114", text.Title);
            Assert.Equal("Slot needed from D114 for 'Survey Kerbin' (Zaltonic Electronics), blocked by timeline.", text.Body);
            Assert.Equal("Slot needed from D114 for a contract, blocked by timeline.",
                ReservationExplanation.ContractSlot(Entry(CommittedFutureKind.ContractAccept, 11400), Fmt).Body);
        }

        [Fact]
        public void Strategies_Activation_Slot_Conflict_Deactivation()
        {
            Assert.Equal("Activated on D114, blocked by timeline until then.",
                ReservationExplanation.StrategyActivation(Entry(CommittedFutureKind.StrategyActivate, 11400), Fmt).Body);
            Assert.Equal("Slot needed from D114 for 'Outsourced R&D', blocked by timeline.",
                ReservationExplanation.StrategySlot(Entry(CommittedFutureKind.StrategyActivate, 11400), "Outsourced R&D", Fmt).Body);
            Assert.Equal("Slot needed from D114 for a strategy, blocked by timeline.",
                ReservationExplanation.StrategySlot(Entry(CommittedFutureKind.StrategyActivate, 11400), null, Fmt).Body);
            Assert.Equal("Conflicts with 'Fundraising' from D114 until D200, blocked by timeline.",
                ReservationExplanation.StrategyConflict(Entry(CommittedFutureKind.StrategyActivate, 11400),
                    Entry(CommittedFutureKind.StrategyDeactivate, 20000), "Fundraising", Fmt).Body);
            Assert.Equal("Conflicts with 'Fundraising' from D114, blocked by timeline.",
                ReservationExplanation.StrategyConflict(Entry(CommittedFutureKind.StrategyActivate, 11400),
                    null, "Fundraising", Fmt).Body);
            Assert.Equal("Deactivated on D400, blocked by timeline until then.",
                ReservationExplanation.StrategyDeactivation(Entry(CommittedFutureKind.StrategyDeactivate, 40000), Fmt).Body);
            Assert.Equal("Activated again on D400, blocked by timeline until then.",
                ReservationExplanation.StrategyDeactivation(Entry(CommittedFutureKind.StrategyActivate, 40000), Fmt).Body);
        }

        [Fact]
        public void FacilityUpgrade_SingleAndTwo()
        {
            var one = ReservationExplanation.FacilityUpgrade(
                new[] { Entry(CommittedFutureKind.FacilityUpgrade, 11400, level: 2) }, Fmt);
            Assert.Equal("Upgraded on D114", one.Title);
            Assert.Equal("Upgraded to level 2 on D114, blocked by timeline until then.", one.Body);
            var two = ReservationExplanation.FacilityUpgrade(
                new[]
                {
                    Entry(CommittedFutureKind.FacilityUpgrade, 11400, "Builder", 2),
                    Entry(CommittedFutureKind.FacilityUpgrade, 20000, "Builder", 3)
                }, Fmt);
            Assert.Equal("Upgraded to level 2 on D114 and to level 3 on D200, blocked by timeline until then.", two.Body);
            Assert.Equal("Upgraded later, blocked by timeline.",
                ReservationExplanation.FacilityUpgrade(new CommittedFutureEntry[0], Fmt).Body);
        }

        [Fact]
        public void KerbalOnFlight_FiniteHold_UntilTheDate()
        {
            var text = ReservationExplanation.KerbalOnFlight(new KerbalHold
            {
                KerbalName = "Jeb", FlightName = "Mun Lander", EndState = KerbalEndState.Recovered,
                ReleaseUT = 13000
            }, Fmt);
            Assert.Equal("Reserved until D130", text.Title);
            Assert.Equal("Reserved by timeline for 'Mun Lander' until D130.", text.Body);
        }

        [Fact]
        public void KerbalOnFlight_Aboard_UntilRecovered_AndNeverNamesALoop()
        {
            var text = ReservationExplanation.KerbalOnFlight(new KerbalHold
            {
                KerbalName = "Jeb", FlightName = "Mun Lander 3", EndState = KerbalEndState.Aboard,
                ReleaseUT = double.PositiveInfinity
            }, Fmt);
            Assert.Equal("Reserved", text.Title);
            Assert.Equal("Reserved by timeline for 'Mun Lander 3' until it is recovered.", text.Body);
            Assert.DoesNotContain("loop", text.Body);
        }

        [Fact]
        public void KerbalOnFlight_NoFlightName_AndAStandInForSomeoneElse()
        {
            var text = ReservationExplanation.KerbalOnFlight(new KerbalHold
            {
                KerbalName = "Lars", SlotOwner = "Jeb", EndState = KerbalEndState.Unknown,
                ReleaseUT = double.PositiveInfinity
            }, Fmt);
            Assert.Equal("Reserved for Jeb", text.Title);
            Assert.Equal("Reserved by timeline in Jeb's seat until that flight is recovered.", text.Body);
            var own = ReservationExplanation.KerbalOnFlight(new KerbalHold
            {
                KerbalName = "Jeb", SlotOwner = "Jeb", FlightName = "X", EndState = KerbalEndState.Aboard,
                ReleaseUT = double.PositiveInfinity
            }, Fmt);
            Assert.Equal("Reserved", own.Title);
            Assert.DoesNotContain("seat", own.Body);
        }

        [Fact]
        public void KerbalLost_FlightAndDeathDate_AndARespawn()
        {
            var lost = ReservationExplanation.KerbalLost("Mun Lander", 94000, double.NaN, Fmt);
            Assert.Equal("Lost", lost.Title);
            Assert.Equal("Lost on 'Mun Lander' on D940.", lost.Body);
            Assert.Equal("Lost.", ReservationExplanation.KerbalLost(null).Body);
            Assert.Equal("Lost on 'Mun Lander'.", ReservationExplanation.KerbalLost("Mun Lander").Body);
            var back = ReservationExplanation.KerbalLost("Mun Lander", 94000, 120000, Fmt);
            Assert.Equal("Lost until D1200", back.Title);
            Assert.Equal("Lost on 'Mun Lander' on D940, back on D1200.", back.Body);
        }

        [Fact]
        public void ExplainKerbalReservation_LostNamesTheDeathFlightAndDate()
        {
            var index = CommittedFutureIndex.Build(
                new List<GameAction>
                {
                    new GameAction
                    {
                        Type = GameActionType.KerbalAssignment, KerbalName = "Jeb",
                        RecordingId = "rec-dead", UT = 100.0, StartUT = 100f, EndUT = 50000f,
                        KerbalEndStateField = KerbalEndState.Dead,
                    },
                },
                id => true, id => "Mun Lander 3", null);
            var hold = new KerbalsModule.KerbalReservation
            {
                KerbalName = "Jeb", ReservedUntilUT = double.PositiveInfinity, IsPermanent = true,
            };
            var text = StockUiReservationPredicates.ExplainKerbalReservation(index, "Jeb", hold, null, Fmt);
            Assert.Equal("Lost on 'Mun Lander 3' on D500.", text.Body);
        }

        [Fact]
        public void StandIn_And_RetiredStandIn()
        {
            Assert.Equal("Standing in for Jebediah Kerman, reserved by timeline for 'Mun Lander'.",
                ReservationExplanation.StandingIn("Jebediah Kerman", "Mun Lander"));
            Assert.Equal("Standing in for Jebediah Kerman, reserved by timeline.",
                ReservationExplanation.StandingIn("Jebediah Kerman", null));
            var retired = ReservationExplanation.KerbalRetiredStandIn("Jebediah Kerman");
            Assert.Equal("Retired", retired.Title);
            Assert.Equal("Retired after standing in for Jebediah Kerman, kept off new crews by timeline.", retired.Body);
        }

        /// <summary>
        /// The owner rules of 2026-09-27 over EVERY builder and variant plus the dismissal
        /// texts: plain ASCII, no raw UT, never "your timeline" / "the timeline" /
        /// "committed", no append-only rule and no way-out part, one sentence per text.
        /// </summary>
        [Fact]
        public void EveryText_FollowsTheOwnersWording_AndNeverSaysYourTimeline()
        {
            var accept = new CommittedFutureEntry(CommittedFutureKind.ContractAccept, "k", 11400, "rec", "Flight",
                title: "Survey", agentTitle: "Agent");
            var texts = new List<ReservationText>
            {
                ReservationExplanation.Tech(Entry(CommittedFutureKind.TechResearch, 11400, "A"), Fmt),
                ReservationExplanation.Tech(Entry(CommittedFutureKind.TechResearch, 11400), Fmt),
                ReservationExplanation.PartPurchase(Entry(CommittedFutureKind.PartPurchase, 11400, "A"), Fmt),
                ReservationExplanation.ContractAccept(Entry(CommittedFutureKind.ContractAccept, 11400), Fmt),
                ReservationExplanation.ContractResolution(Entry(CommittedFutureKind.ContractComplete, 11400, "A"), Fmt),
                ReservationExplanation.ContractResolution(Entry(CommittedFutureKind.ContractFail, 11400), Fmt),
                ReservationExplanation.ContractResolution(Entry(CommittedFutureKind.ContractCancel, 11400), Fmt),
                ReservationExplanation.ContractSlot(accept, Fmt),
                ReservationExplanation.ContractSlot(null, Fmt),
                ReservationExplanation.FacilityUpgrade(new[] { Entry(CommittedFutureKind.FacilityUpgrade, 11400, "A", 2) }, Fmt),
                ReservationExplanation.FacilityUpgrade(new CommittedFutureEntry[0], Fmt),
                ReservationExplanation.FacilityRepair(new[] { Entry(CommittedFutureKind.FacilityRepair, 11400, "A") }, Fmt),
                ReservationExplanation.FacilityRepair(new CommittedFutureEntry[0], Fmt),
                ReservationExplanation.StrategyActivation(Entry(CommittedFutureKind.StrategyActivate, 11400, "A"), Fmt),
                ReservationExplanation.StrategySlot(Entry(CommittedFutureKind.StrategyActivate, 11400), "S", Fmt),
                ReservationExplanation.StrategyConflict(Entry(CommittedFutureKind.StrategyActivate, 11400), null, "S", Fmt),
                ReservationExplanation.StrategyDeactivation(Entry(CommittedFutureKind.StrategyDeactivate, 11400), Fmt),
                ReservationExplanation.KerbalHire(Entry(CommittedFutureKind.KerbalHire, 11400), Fmt),
                ReservationExplanation.KerbalRetire(Entry(CommittedFutureKind.KerbalRetire, 11400), Fmt),
                ReservationExplanation.KerbalOnFlight(new KerbalHold { ReleaseUT = 500, EndState = KerbalEndState.Recovered }, Fmt),
                ReservationExplanation.KerbalOnFlight(new KerbalHold { KerbalName = "L", SlotOwner = "J", FlightName = "F",
                    ReleaseUT = double.PositiveInfinity }, Fmt),
                ReservationExplanation.KerbalRetiredStandIn("J"),
                ReservationExplanation.KerbalRetiredStandIn(null),
                ReservationExplanation.KerbalLost("A", 500, 900, Fmt),
                ReservationExplanation.KerbalLost(null)
            };
            var bodies = new List<string>();
            foreach (var t in texts)
            {
                Assert.True(string.IsNullOrEmpty(t.Rule) && string.IsNullOrEmpty(t.WayOut), "a rule or way-out part in: " + t.Body);
                bodies.Add(t.Body);
                bodies.Add(t.Title);
            }
            bodies.Add(ReservationExplanation.StandingIn("J", "F"));
            bodies.Add(KerbalDismissalPatch.DescribeDismissalBlock(KerbalReservationKind.NotManaged, true));
            bodies.Add(KerbalDismissalPatch.DescribeDismissalBlock(KerbalReservationKind.ReservedActive));
            bodies.Add(KerbalDismissalPatch.DescribeDismissalBlock(KerbalReservationKind.ReservedRetired));
            bodies.Add(KerbalDismissalPatch.DescribeDismissalBlock(KerbalReservationKind.NotManaged, "J", "F"));
            bodies.Add(KerbalDismissalPatch.DescribeDismissalBlock(KerbalReservationKind.NotManaged));

            foreach (string body in bodies)
            {
                Assert.False(string.IsNullOrEmpty(body));
                foreach (char c in body)
                    Assert.True(c < 128, "non-ASCII char in: " + body);
                Assert.DoesNotContain("UT ", body);
                Assert.DoesNotContain("your timeline", body, StringComparison.OrdinalIgnoreCase);
                Assert.DoesNotContain("the timeline", body, StringComparison.OrdinalIgnoreCase);
                Assert.DoesNotContain("committed", body, StringComparison.OrdinalIgnoreCase);
                Assert.DoesNotContain("fixed once", body);
                // One sentence: at most one period, and only at the end.
                int dot = body.IndexOf('.');
                Assert.True(dot < 0 || dot == body.Length - 1, "more than one sentence in: " + body);
            }
        }

        [Fact]
        public void FormatDate_FallsBackToInvariantUT_OnlyWithoutAFormatter()
        {
            Assert.Equal("D5", ReservationExplanation.FormatDate(500, Fmt));
            Assert.Equal("UT 500", ReservationExplanation.FormatDate(500.4, null));
        }
    }
}
