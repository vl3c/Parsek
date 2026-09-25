using System;
using System.Collections.Generic;
using System.Globalization;
using Xunit;

namespace Parsek.Tests
{
    /// <summary>
    /// The section 6 explanation texts (ReservationExplanation.cs): fact + rule + when the
    /// item frees up, for every kind and variant, with a deterministic date formatter.
    /// </summary>
    public class ReservationExplanationTests
    {
        // "Y<year> D<day>" from whole days, so the tests read like KSPUtil.PrintDateCompact.
        private static readonly Func<double, string> Fmt =
            ut => "D" + ((long)(ut / 100.0)).ToString(CultureInfo.InvariantCulture);

        private const string Rule =
            "Parsek's timeline is fixed once committed, so this cannot happen earlier or twice.";

        private static CommittedFutureEntry Entry(CommittedFutureKind kind, double ut, string flight = null, int level = 0)
        {
            return new CommittedFutureEntry(kind, "k", ut, flight != null ? "rec" : null, flight, facilityToLevel: level);
        }

        [Fact]
        public void Tech_NamesTheFlight_AndSaysWhenItUnlocks()
        {
            var text = ReservationExplanation.Tech(Entry(CommittedFutureKind.TechResearch, 11400, "Mun Lander 3"), Fmt);

            Assert.Equal("Researched on D114", text.Title);
            Assert.Equal(
                "Researched on D114 by the committed flight 'Mun Lander 3'. " + Rule + " It unlocks on that date.",
                text.Body);
        }

        [Fact]
        public void Tech_WithoutAFlight_SaysOnYourCommittedTimeline()
        {
            var text = ReservationExplanation.Tech(Entry(CommittedFutureKind.TechResearch, 11400), Fmt);
            Assert.StartsWith("Researched on D114 on your committed timeline.", text.Body);
        }

        [Fact]
        public void ContractAccept_SaysItBecomesActiveOnThatDate()
        {
            var text = ReservationExplanation.ContractAccept(Entry(CommittedFutureKind.ContractAccept, 11400, "Mun Lander 3"), Fmt);
            Assert.Equal("Accepted on D114", text.Title);
            Assert.Equal(
                "Accepted on D114 by the committed flight 'Mun Lander 3'. " + Rule + " It becomes active on that date.",
                text.Body);
        }

        [Fact]
        public void FacilityUpgrade_Single()
        {
            var text = ReservationExplanation.FacilityUpgrade(
                new[] { Entry(CommittedFutureKind.FacilityUpgrade, 11400, level: 2) }, Fmt);
            Assert.Equal("Upgraded on D114", text.Title);
            Assert.Equal(
                "Upgraded to level 2 on D114 on your committed timeline. " + Rule + " The upgrade happens on that date.",
                text.Body);
        }

        [Fact]
        public void FacilityUpgrade_Two_ListsBothDates()
        {
            var text = ReservationExplanation.FacilityUpgrade(
                new[]
                {
                    Entry(CommittedFutureKind.FacilityUpgrade, 11400, level: 2),
                    Entry(CommittedFutureKind.FacilityUpgrade, 20000, level: 3)
                }, Fmt);
            Assert.Equal(
                "Upgraded to level 2 on D114 and to level 3 on D200 on your committed timeline. " + Rule
                + " The upgrades happen on those dates.",
                text.Body);
        }

        [Fact]
        public void FacilityUpgrade_TwoByOneFlight_NamesIt()
        {
            var text = ReservationExplanation.FacilityUpgrade(
                new[]
                {
                    Entry(CommittedFutureKind.FacilityUpgrade, 11400, "Builder", 2),
                    Entry(CommittedFutureKind.FacilityUpgrade, 20000, "Builder", 3)
                }, Fmt);
            Assert.StartsWith("Upgraded to level 2 on D114 and to level 3 on D200 by the committed flight 'Builder'.", text.Body);
        }

        [Fact]
        public void KerbalHire_SaysTheyJoinTheRosterThen()
        {
            var text = ReservationExplanation.KerbalHire(Entry(CommittedFutureKind.KerbalHire, 11400), Fmt);
            Assert.Equal("Hired on D114", text.Title);
            Assert.Equal("Hired on D114 on your committed timeline. " + Rule + " They join the roster on that date.", text.Body);
        }

        [Fact]
        public void KerbalRetire_IsWordedDismissed_NotTheWindowsRetired()
        {
            var text = ReservationExplanation.KerbalRetire(Entry(CommittedFutureKind.KerbalRetire, 11400), Fmt);
            Assert.Equal("Dismissed on D114", text.Title);
            Assert.Equal("Dismissed on D114 on your committed timeline. " + Rule + " They leave the roster on that date.", text.Body);
        }

        private const string CrewRule = "A kerbal on a committed flight cannot be used or risked before it ends.";

        [Fact]
        public void KerbalOnFlight_FiniteHold_FreeAfterTheDate()
        {
            var text = ReservationExplanation.KerbalOnFlight(new KerbalHold
            {
                KerbalName = "Jeb", FlightName = "Mun Lander 3", EndState = KerbalEndState.Recovered,
                ReleaseUT = 13000, FlightEndUT = 13000
            }, Fmt);
            Assert.Equal("Reserved until D130", text.Title);
            Assert.Equal("Flies 'Mun Lander 3' on your committed timeline. " + CrewRule + " Free after D130.", text.Body);
        }

        [Fact]
        public void KerbalOnFlight_Aboard_FreeOnceRecovered()
        {
            var text = ReservationExplanation.KerbalOnFlight(new KerbalHold
            {
                KerbalName = "Jeb", FlightName = "Mun Lander 3", EndState = KerbalEndState.Aboard,
                ReleaseUT = double.PositiveInfinity, FlightEndUT = 13000
            }, Fmt);
            Assert.Equal("Reserved", text.Title);
            Assert.Equal("Flies 'Mun Lander 3' on your committed timeline. " + CrewRule
                + " Free once 'Mun Lander 3' is recovered.", text.Body);
        }

        [Fact]
        public void KerbalOnFlight_RecoveredInALoop_StoppingTheLoopFreesThem()
        {
            var text = ReservationExplanation.KerbalOnFlight(new KerbalHold
            {
                KerbalName = "Jeb", FlightName = "Mun Lander 3", EndState = KerbalEndState.Recovered,
                ReleaseUT = double.PositiveInfinity, IsLooping = true, FlightEndUT = 13000
            }, Fmt);
            Assert.Equal("Flies 'Mun Lander 3' on your committed timeline. " + CrewRule
                + " Held while 'Mun Lander 3' loops. Stopping its loop frees them after D130.", text.Body);
        }

        [Fact]
        public void KerbalOnFlight_OpenEndedRecovered_IsALoopHoldEvenWithTheFlagOff()
        {
            // A Recovered hold is finite unless its chain looped at the last walk.
            var text = ReservationExplanation.KerbalOnFlight(new KerbalHold
            {
                KerbalName = "Jeb", FlightName = "Mun Lander 3", EndState = KerbalEndState.Recovered,
                ReleaseUT = double.PositiveInfinity, IsLooping = false, FlightEndUT = double.NaN
            }, Fmt);
            Assert.EndsWith("Held while 'Mun Lander 3' loops. Stopping its loop frees them when it ends.", text.Body);
        }

        [Fact]
        public void KerbalOnFlight_AboardInALoop_TheLoopThenARecoveryEndIt()
        {
            var text = ReservationExplanation.KerbalOnFlight(new KerbalHold
            {
                KerbalName = "Val", FlightName = "Station Hop", EndState = KerbalEndState.Aboard,
                ReleaseUT = double.PositiveInfinity, IsLooping = true
            }, Fmt);
            Assert.EndsWith("Held while 'Station Hop' loops, and then until it is recovered.", text.Body);
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
            Assert.Equal("Flies a committed flight in Jeb's seat on your timeline. " + CrewRule
                + " Free once that flight's vessel is recovered.", text.Body);
        }

        [Fact]
        public void KerbalOnFlight_OwnSlot_IsNotReservedForHimself()
        {
            var text = ReservationExplanation.KerbalOnFlight(new KerbalHold
            {
                KerbalName = "Jeb", SlotOwner = "Jeb", FlightName = "X", EndState = KerbalEndState.Aboard,
                ReleaseUT = double.PositiveInfinity
            }, Fmt);
            Assert.Equal("Reserved", text.Title);
            Assert.DoesNotContain("seat", text.Body);
        }

        [Fact]
        public void KerbalLost_UsesTheKerbalsWindowRemedy()
        {
            var named = ReservationExplanation.KerbalLost("Mun Lander 3");
            Assert.Equal("Lost", named.Title);
            Assert.Equal("Lost on the committed flight 'Mun Lander 3'. That flight is fixed history. "
                + KerbalsPresentation.LostReFlyRemedy, named.Body);
            Assert.StartsWith("Lost on a committed flight.", ReservationExplanation.KerbalLost(null).Body);
        }

        [Fact]
        public void EveryText_IsPlainAscii_WithNoRawUT()
        {
            var texts = new List<ReservationText>
            {
                ReservationExplanation.Tech(Entry(CommittedFutureKind.TechResearch, 11400, "A"), Fmt),
                ReservationExplanation.ContractAccept(Entry(CommittedFutureKind.ContractAccept, 11400), Fmt),
                ReservationExplanation.FacilityUpgrade(new[] { Entry(CommittedFutureKind.FacilityUpgrade, 11400, level: 2) }, Fmt),
                ReservationExplanation.KerbalHire(Entry(CommittedFutureKind.KerbalHire, 11400), Fmt),
                ReservationExplanation.KerbalRetire(Entry(CommittedFutureKind.KerbalRetire, 11400), Fmt),
                ReservationExplanation.KerbalOnFlight(new KerbalHold { ReleaseUT = 500, EndState = KerbalEndState.Recovered }, Fmt),
                ReservationExplanation.KerbalLost("A")
            };
            foreach (var t in texts)
            {
                foreach (char c in t.Title + t.Body)
                    Assert.True(c < 128, "non-ASCII char in: " + t.Body);
                Assert.DoesNotContain("UT ", t.Body);
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
