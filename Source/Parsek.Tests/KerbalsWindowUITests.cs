using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Threading;
using Xunit;

namespace Parsek.Tests
{
    /// <summary>
    /// The Kerbals window's two column tables, as rebuilt 2026-09-15: the pure
    /// <see cref="KerbalsPresentation"/> builders (every status word, both fold
    /// partitions, the stand-in attribution, the date fallback and both sort orders),
    /// the window's own formatters, its fold / expand seam accessors and the two
    /// cross-link helpers.
    ///
    /// <para>This class REPLACED the 43 cells that pinned the retired indented-outline
    /// strings (<c>FormatOwnerHeader</c>, <c>FormatEndStateRow</c>,
    /// <c>FormatKerbalSummary</c>, the rich-text Mission Outcomes header). Coverage moved
    /// rather than shrank: every string those cells pinned has a successor cell here
    /// naming the new wording. Contract: <c>docs/dev/design-gui-kerbals-window.md</c>.</para>
    /// </summary>
    [Collection("Sequential")]
    public class KerbalsWindowUITests : IDisposable
    {
        private readonly List<string> logLines = new List<string>();

        public KerbalsWindowUITests()
        {
            ParsekLog.ResetTestOverrides();
            ParsekLog.SuppressLogging = false;
            ParsekLog.TestSinkForTesting = line => logLines.Add(line);
            // One cell below constructs a ParsekUI, which writes the static activeInstance and
            // re-seeds the static applied-mode latch. Reset both ends - the bracket
            // TimelineGoToMissionTests and the UiComplexityMode classes use - so a leaked
            // instance from an earlier class cannot receive this class's mode hooks, and so
            // this class cannot leave a live one for the next.
            ParsekUI.ResetUiComplexityModeForTesting();
        }

        public void Dispose()
        {
            ParsekUI.ResetUiComplexityModeForTesting();
            ParsekLog.ResetTestOverrides();
            ParsekLog.SuppressLogging = true;
        }

        // ------------------------------------------------------------------
        // Helpers
        // ------------------------------------------------------------------

        private static KerbalsModule.KerbalSlot Slot(
            string owner, string trait, List<string> chain, bool permanentlyGone = false)
        {
            return new KerbalsModule.KerbalSlot
            {
                OwnerName = owner,
                OwnerTrait = trait,
                OwnerPermanentlyGone = permanentlyGone,
                Chain = chain ?? new List<string>()
            };
        }

        private static KerbalsModule.KerbalReservation Res(
            string name, double untilUT, bool permanent = false)
        {
            return new KerbalsModule.KerbalReservation
            {
                KerbalName = name,
                ReservedUntilUT = untilUT,
                IsPermanent = permanent
            };
        }

        private static KerbalsPresentation.RosterKerbal Kerbal(
            string name, string trait, string vessel = null)
        {
            return new KerbalsPresentation.RosterKerbal
            {
                Name = name,
                Trait = trait,
                AssignedVesselName = vessel
            };
        }

        // Mirrors KerbalsModule.GetActiveChainIndex(KerbalSlot) semantics for the pure
        // builders.
        private static KerbalsWindowUI.ActiveChainIndexFunc ActiveChainIndexLike(
            IReadOnlyDictionary<string, KerbalsModule.KerbalReservation> reservations)
        {
            return slot =>
            {
                if (slot == null) return KerbalsModule.NoActiveChainOccupant;
                if (slot.OwnerPermanentlyGone) return KerbalsModule.NoActiveChainOccupant;
                if (!reservations.ContainsKey(slot.OwnerName)) return KerbalsModule.ActiveOwnerIndex;
                for (int i = 0; i < slot.Chain.Count; i++)
                {
                    string s = slot.Chain[i];
                    if (s == null || !reservations.ContainsKey(s))
                        return i;
                }
                return slot.Chain.Count;
            };
        }

        // A deterministic stand-in for KSPUtil.PrintDateCompact: "D<n>", so a date cell is
        // recognisable in an assertion and carries no culture-sensitive separator of its
        // own (the invariance of the PRODUCTION fallback has its own cell below).
        private static readonly Func<double, string> FakeDate =
            ut => "D" + ut.ToString("F0", CultureInfo.InvariantCulture);

        private static Recording RecWithEndStates(
            string id,
            string vesselName,
            double endUT,
            Dictionary<string, KerbalEndState> endStates,
            bool resolved = true)
        {
            return new Recording
            {
                RecordingId = id,
                VesselName = vesselName,
                ExplicitEndUT = endUT,
                CrewEndStates = endStates,
                CrewEndStatesResolved = resolved
            };
        }

        /// <summary>
        /// One recorded SEGMENT. <paramref name="treeId"/> left null makes the segment its
        /// own mission (the standalone-recording contract), which is what every cell that
        /// is not about the mission collapse wants; <paramref name="startUT"/> left null
        /// defaults to the end UT, so a one-instant segment needs no second number.
        /// </summary>
        private static KerbalsWindowUI.CrewEndStateEntry Entry(
            string kerbal, string recordingId, string recordingName, double endUT,
            KerbalEndState state, string treeId = null, double? startUT = null)
        {
            return new KerbalsWindowUI.CrewEndStateEntry
            {
                KerbalName = kerbal,
                RecordingId = recordingId,
                RecordingName = recordingName,
                TreeId = treeId,
                StartUT = startUT ?? endUT,
                EndUT = endUT,
                EndState = state
            };
        }

        private static Dictionary<string, IReadOnlyCollection<string>> RawCrew(
            string recordingId, params string[] names)
        {
            return new Dictionary<string, IReadOnlyCollection<string>>(StringComparer.Ordinal)
            {
                { recordingId, new List<string>(names) }
            };
        }

        // ------------------------------------------------------------------
        // BuildEndStates - the flat, sorted per-recording crew end states
        // ------------------------------------------------------------------

        [Fact]
        public void BuildEndStates_NullRecordings_ReturnsEmpty()
        {
            Assert.Empty(KerbalsWindowUI.BuildEndStates(null));
        }

        [Fact]
        public void BuildEndStates_GroupsByKerbalThenSortsChronologically()
        {
            var recs = new List<Recording>
            {
                RecWithEndStates("r2", "Late", 900.0, new Dictionary<string, KerbalEndState>
                    { { "Jebediah Kerman", KerbalEndState.Aboard } }),
                RecWithEndStates("r1", "Early", 100.0, new Dictionary<string, KerbalEndState>
                    { { "Jebediah Kerman", KerbalEndState.Recovered } }),
            };

            List<KerbalsWindowUI.CrewEndStateEntry> states =
                KerbalsWindowUI.BuildEndStates(recs);

            Assert.Equal(2, states.Count);
            Assert.Equal("r1", states[0].RecordingId);
            Assert.Equal("r2", states[1].RecordingId);
        }

        [Fact]
        public void BuildEndStates_MultipleKerbals_OrderedOrdinallyByName()
        {
            var recs = new List<Recording>
            {
                RecWithEndStates("r1", "Shared", 50.0, new Dictionary<string, KerbalEndState>
                {
                    { "Valentina Kerman", KerbalEndState.Recovered },
                    { "Bill Kerman", KerbalEndState.Recovered },
                }),
            };

            List<KerbalsWindowUI.CrewEndStateEntry> states =
                KerbalsWindowUI.BuildEndStates(recs);

            Assert.Equal(new[] { "Bill Kerman", "Valentina Kerman" },
                states.Select(s => s.KerbalName).ToArray());
        }

        [Fact]
        public void BuildEndStates_SkipsUnresolvedAndNullDicts()
        {
            var recs = new List<Recording>
            {
                RecWithEndStates("pending", "Pending", 10.0,
                    new Dictionary<string, KerbalEndState>
                        { { "Bill Kerman", KerbalEndState.Aboard } },
                    resolved: false),
                RecWithEndStates("nodict", "NoDict", 20.0, null),
                RecWithEndStates("good", "Good", 30.0,
                    new Dictionary<string, KerbalEndState>
                        { { "Bill Kerman", KerbalEndState.Recovered } }),
            };

            List<KerbalsWindowUI.CrewEndStateEntry> states =
                KerbalsWindowUI.BuildEndStates(recs);

            Assert.Single(states);
            Assert.Equal("good", states[0].RecordingId);
        }

        // ------------------------------------------------------------------
        // The Flights tab
        // ------------------------------------------------------------------

        private static List<KerbalsPresentation.FlightGroup> Flights(
            IReadOnlyList<KerbalsWindowUI.CrewEndStateEntry> entries,
            IReadOnlyDictionary<string, string> missionNames = null,
            IReadOnlyDictionary<string, IReadOnlyCollection<string>> rawCrew = null,
            IReadOnlyDictionary<string, string> replacements = null,
            IReadOnlyDictionary<string, KerbalsModule.KerbalSlot> slots = null,
            IReadOnlyDictionary<string, string> traits = null,
            Func<double, string> formatDate = null)
        {
            return KerbalsPresentation.BuildFlightRows(
                entries, missionNames, rawCrew, replacements, slots, traits,
                formatDate ?? FakeDate);
        }

        [Fact]
        public void Flights_NoEndStates_ReturnsNoGroups()
        {
            Assert.Empty(Flights(new List<KerbalsWindowUI.CrewEndStateEntry>()));
            Assert.Empty(Flights(null));
        }

        [Fact]
        public void Flights_OneKerbal_RowsAreChronologicalAndCarryEveryCell()
        {
            var entries = new List<KerbalsWindowUI.CrewEndStateEntry>
            {
                Entry("Jebediah Kerman", "r2", "Mun Hopper", 900.0, KerbalEndState.Aboard),
                Entry("Jebediah Kerman", "r1", "Jumping Flea", 100.0, KerbalEndState.Recovered),
            };
            var missions = new Dictionary<string, string>(StringComparer.Ordinal)
                { { "r1", "First Steps" } };

            List<KerbalsPresentation.FlightGroup> groups = Flights(entries, missions);

            Assert.Single(groups);
            KerbalsPresentation.FlightGroup g = groups[0];
            Assert.Equal("Jebediah Kerman", g.KerbalName);
            Assert.Equal(2, g.Rows.Count);
            // Chronological, and the Mission column prefers the mission NAME, falling back
            // to the recorded vessel name.
            Assert.Equal("D100", g.Rows[0].DateText);
            Assert.Equal("First Steps", g.Rows[0].MissionText);
            Assert.Equal("Recovered", g.Rows[0].OutcomeText);
            Assert.Equal("D900", g.Rows[1].DateText);
            Assert.Equal("Mun Hopper", g.Rows[1].MissionText);
            Assert.Equal("Still aboard", g.Rows[1].OutcomeText);
            // Nobody stood in, so the Mission cell is the mission name alone.
            Assert.Null(g.Rows[0].StandInName);
            Assert.Equal("First Steps", g.Rows[0].MissionCellText);
        }

        [Fact]
        public void Flights_MissionNameFallsBackToRecordingNameThenUnnamed()
        {
            var entries = new List<KerbalsWindowUI.CrewEndStateEntry>
            {
                Entry("Bill Kerman", "r1", "", 10.0, KerbalEndState.Recovered),
            };

            List<KerbalsPresentation.FlightGroup> groups = Flights(entries);

            Assert.Equal("(unnamed)", groups[0].Rows[0].MissionText);
        }

        [Fact]
        public void Flights_GroupsOrderedOrdinallyByKerbalName()
        {
            var entries = new List<KerbalsWindowUI.CrewEndStateEntry>
            {
                Entry("Valentina Kerman", "r1", "A", 10.0, KerbalEndState.Recovered),
                Entry("Bill Kerman", "r1", "A", 10.0, KerbalEndState.Recovered),
            };

            List<KerbalsPresentation.FlightGroup> groups = Flights(entries);

            Assert.Equal(new[] { "Bill Kerman", "Valentina Kerman" },
                groups.Select(g => g.KerbalName).ToArray());
        }

        [Theory]
        [InlineData(KerbalEndState.Recovered, "Recovered")]
        [InlineData(KerbalEndState.Dead, "Lost")]
        [InlineData(KerbalEndState.Aboard, "Still aboard")]
        [InlineData(KerbalEndState.Unknown, "Outcome unknown")]
        public void Flights_OutcomeVocabularyIsFourWordsWide(
            KerbalEndState state, string expected)
        {
            Assert.Equal(expected, KerbalsPresentation.FormatOutcome(state));
        }

        [Fact]
        public void Flights_GroupHeader_SingleMissionReadsSingularAndCarriesTheTrait()
        {
            var rows = new List<KerbalsPresentation.FlightRow>
            {
                new KerbalsPresentation.FlightRow { EndState = KerbalEndState.Recovered },
            };

            Assert.Equal("Bill Kerman [Engineer] - 1 mission: 1 recovered",
                KerbalsPresentation.FormatFlightGroupHeader("Bill Kerman", "Engineer", rows));
            // No known trait: the bracket is dropped rather than rendered empty.
            Assert.Equal("Bill Kerman - 1 mission: 1 recovered",
                KerbalsPresentation.FormatFlightGroupHeader("Bill Kerman", "", rows));
        }

        [Fact]
        public void Flights_GroupHeader_OmitsZeroBucketsAndOrdersThem()
        {
            var rows = new List<KerbalsPresentation.FlightRow>
            {
                new KerbalsPresentation.FlightRow { EndState = KerbalEndState.Unknown },
                new KerbalsPresentation.FlightRow { EndState = KerbalEndState.Dead },
                new KerbalsPresentation.FlightRow { EndState = KerbalEndState.Recovered },
                new KerbalsPresentation.FlightRow { EndState = KerbalEndState.Recovered },
            };

            // Recovered, lost, aboard, unknown - in that order, with the empty bucket gone.
            Assert.Equal(
                "Jebediah Kerman [Pilot] - 4 missions: 2 recovered, 1 lost, 1 unknown",
                KerbalsPresentation.FormatFlightGroupHeader(
                    "Jebediah Kerman", "Pilot", rows));
        }

        [Fact]
        public void Flights_GroupHeader_IsBuiltWhetherOrNotTheGroupIsFolded()
        {
            // The ruling: the bucket summary IS the header, not a folded-only extra.
            var entries = new List<KerbalsWindowUI.CrewEndStateEntry>
            {
                Entry("Bill Kerman", "r1", "A", 10.0, KerbalEndState.Recovered),
            };
            var traits = new Dictionary<string, string>(StringComparer.Ordinal)
                { { "Bill Kerman", "Engineer" } };

            List<KerbalsPresentation.FlightGroup> groups = Flights(entries, traits: traits);

            Assert.Equal("Bill Kerman [Engineer] - 1 mission: 1 recovered",
                groups[0].HeaderText);
        }

        [Fact]
        public void Flights_DateCell_FallsBackToInvariantRawUtWithNoFormatter()
        {
            // The builder is called DIRECTLY here: the Flights() helper substitutes its
            // fake formatter for a null, and this cell is about the no-formatter path -
            // which must spell the raw UT invariantly rather than pick up the host culture.
            var entries = new List<KerbalsWindowUI.CrewEndStateEntry>
            {
                Entry("Bill Kerman", "r1", "A", 12045.6, KerbalEndState.Recovered),
            };

            List<KerbalsPresentation.FlightGroup> groups =
                KerbalsPresentation.BuildFlightRows(
                    entries, null, null, null, null, null, null);

            Assert.Equal("12046", groups[0].Rows[0].DateText);
        }

        // ------------------------------------------------------------------
        // Stand-in attribution ("as <name>")
        // ------------------------------------------------------------------

        /// <summary>Jeb reserved with a finite return, which is what makes the first
        /// free member of his chain the ACTIVE occupant - the only chain status that reads
        /// as a stand-in.</summary>
        private static Dictionary<string, KerbalsModule.KerbalReservation> JebReserved(
            double untilUT = 500.0)
        {
            return new Dictionary<string, KerbalsModule.KerbalReservation>(
                StringComparer.Ordinal)
            {
                { "Jebediah Kerman", Res("Jebediah Kerman", untilUT) },
            };
        }

        private static Dictionary<string, KerbalsModule.KerbalSlot> JebCoveredByLars()
        {
            return new Dictionary<string, KerbalsModule.KerbalSlot>(StringComparer.Ordinal)
            {
                { "Jebediah Kerman",
                    Slot("Jebediah Kerman", "Pilot", new List<string> { "Lars Kerman" }) },
            };
        }

        [Fact]
        public void StandIn_RawRecordingCrewIsThePerFlightSource()
        {
            var entries = new List<KerbalsWindowUI.CrewEndStateEntry>
            {
                Entry("Jebediah Kerman", "r1", "Mun Hopper", 100.0, KerbalEndState.Recovered),
            };

            List<KerbalsPresentation.FlightGroup> groups = Flights(
                entries, rawCrew: RawCrew("r1", "Lars Kerman"), slots: JebCoveredByLars());

            Assert.Equal("Lars Kerman", groups[0].Rows[0].StandInName);
        }

        [Fact]
        public void StandIn_OwnerAboardMeansNoNoteEvenWithAChain()
        {
            var entries = new List<KerbalsWindowUI.CrewEndStateEntry>
            {
                Entry("Jebediah Kerman", "r1", "Mun Hopper", 100.0, KerbalEndState.Recovered),
            };

            List<KerbalsPresentation.FlightGroup> groups = Flights(
                entries,
                rawCrew: RawCrew("r1", "Jebediah Kerman", "Lars Kerman"),
                slots: JebCoveredByLars());

            Assert.Null(groups[0].Rows[0].StandInName);
        }

        [Fact]
        public void StandIn_ARawCrewmateOutsideTheChainIsNotReadAsAStandIn()
        {
            var entries = new List<KerbalsWindowUI.CrewEndStateEntry>
            {
                Entry("Jebediah Kerman", "r1", "Mun Hopper", 100.0, KerbalEndState.Recovered),
            };

            // Bob flew along; he is nobody's stand-in.
            List<KerbalsPresentation.FlightGroup> groups = Flights(
                entries, rawCrew: RawCrew("r1", "Bob Kerman"), slots: JebCoveredByLars());

            Assert.Null(groups[0].Rows[0].StandInName);
        }

        [Fact]
        public void StandIn_ReplacementMapIsTheFallbackWhenTheRecordingHasNoRawCrew()
        {
            var entries = new List<KerbalsWindowUI.CrewEndStateEntry>
            {
                Entry("Jebediah Kerman", "r1", "Mun Hopper", 100.0, KerbalEndState.Recovered),
            };
            var replacements = new Dictionary<string, string>(StringComparer.Ordinal)
                { { "Jebediah Kerman", "Lars Kerman" } };

            List<KerbalsPresentation.FlightGroup> groups = Flights(
                entries, rawCrew: null, replacements: replacements,
                slots: JebCoveredByLars());

            Assert.Equal("Lars Kerman", groups[0].Rows[0].StandInName);
        }

        [Fact]
        public void StandIn_RawCrewWinsOverTheReplacementMap()
        {
            // The map says Lars covers Jeb NOW; this flight's own crew says Hanley did.
            var slots = new Dictionary<string, KerbalsModule.KerbalSlot>(StringComparer.Ordinal)
            {
                { "Jebediah Kerman", Slot("Jebediah Kerman", "Pilot",
                    new List<string> { "Hanley Kerman", "Lars Kerman" }) },
            };
            var entries = new List<KerbalsWindowUI.CrewEndStateEntry>
            {
                Entry("Jebediah Kerman", "r1", "Mun Hopper", 100.0, KerbalEndState.Recovered),
            };
            var replacements = new Dictionary<string, string>(StringComparer.Ordinal)
                { { "Jebediah Kerman", "Lars Kerman" } };

            List<KerbalsPresentation.FlightGroup> groups = Flights(
                entries, rawCrew: RawCrew("r1", "Hanley Kerman"),
                replacements: replacements, slots: slots);

            Assert.Equal("Hanley Kerman", groups[0].Rows[0].StandInName);
        }

        [Fact]
        public void StandIn_NoSlotMeansNoNoteWhateverTheMapSays()
        {
            var entries = new List<KerbalsWindowUI.CrewEndStateEntry>
            {
                Entry("Jebediah Kerman", "r1", "Mun Hopper", 100.0, KerbalEndState.Recovered),
            };
            var replacements = new Dictionary<string, string>(StringComparer.Ordinal)
                { { "Jebediah Kerman", "Lars Kerman" } };

            List<KerbalsPresentation.FlightGroup> groups = Flights(
                entries, replacements: replacements, slots: null);

            Assert.Null(groups[0].Rows[0].StandInName);
        }

        // ------------------------------------------------------------------
        // The Roster tab
        // ------------------------------------------------------------------

        private static KerbalsPresentation.RosterRowSet Roster(
            IReadOnlyList<KerbalsPresentation.RosterKerbal> roster,
            IReadOnlyDictionary<string, KerbalsModule.KerbalSlot> slots = null,
            IReadOnlyDictionary<string, KerbalsModule.KerbalReservation> reservations = null,
            IReadOnlyList<string> retired = null,
            IReadOnlyList<KerbalsPresentation.FlightGroup> flights = null)
        {
            var res = reservations
                      ?? new Dictionary<string, KerbalsModule.KerbalReservation>(
                          StringComparer.Ordinal);
            return KerbalsPresentation.BuildRosterRows(
                roster, slots, res, retired, flights, ActiveChainIndexLike(res), FakeDate);
        }

        private static KerbalsPresentation.RosterRow Find(
            KerbalsPresentation.RosterRowSet set, string name)
        {
            foreach (KerbalsPresentation.RosterRow r in set.Involved)
                if (r.Name == name) return r;
            foreach (KerbalsPresentation.RosterRow r in set.Plain)
                if (r.Name == name) return r;
            throw new InvalidOperationException("no roster row for " + name);
        }

        [Fact]
        public void Roster_PlainAvailableKerbalsLandInTheFoldedPartition()
        {
            var roster = new List<KerbalsPresentation.RosterKerbal>
            {
                Kerbal("Bill Kerman", "Engineer"),
                Kerbal("Bob Kerman", "Scientist"),
            };

            KerbalsPresentation.RosterRowSet set = Roster(roster);

            Assert.Empty(set.Involved);
            Assert.Equal(2, set.Plain.Count);
            Assert.Equal("Available", set.Plain[0].StatusText);
            Assert.Equal(KerbalsPresentation.EmptyCell, set.Plain[0].LastFlightText);
            Assert.Equal("Available, no recorded flights (2)",
                KerbalsPresentation.FormatPlainFoldHeader(set.Plain.Count));
        }

        [Fact]
        public void Roster_AKerbalWithAFlightIsListedRatherThanFolded()
        {
            var roster = new List<KerbalsPresentation.RosterKerbal>
                { Kerbal("Bill Kerman", "Engineer") };
            var entries = new List<KerbalsWindowUI.CrewEndStateEntry>
            {
                Entry("Bill Kerman", "r1", "Jumping Flea", 340.0, KerbalEndState.Recovered),
            };

            KerbalsPresentation.RosterRowSet set =
                Roster(roster, flights: Flights(entries));

            Assert.Single(set.Involved);
            Assert.Empty(set.Plain);
            Assert.Equal("Jumping Flea - Recovered", set.Involved[0].LastFlightText);
            Assert.True(set.Involved[0].HasFlights);
        }

        [Fact]
        public void Roster_AnAssignedKerbalStaysListedWithNoHistory()
        {
            // "Aboard something right now" is the one stock fact the window is asked for,
            // so it does not collapse into the plain bucket.
            var roster = new List<KerbalsPresentation.RosterKerbal>
                { Kerbal("Valentina Kerman", "Pilot", "Station Alpha") };

            KerbalsPresentation.RosterRowSet set = Roster(roster);

            Assert.Single(set.Involved);
            Assert.Equal("Assigned (Station Alpha)", set.Involved[0].StatusText);
            Assert.Empty(set.Plain);
        }

        // catches: the reservation cell promising a release date. The reservation a
        // committed flight creates does not lift when that date passes
        // (KerbalReservationReleaseTests), so the cell names the flight, never a date,
        // and the hover carries the release rule.
        [Fact]
        public void Roster_AReservedOwnerNamesTheFlightThatHoldsHimAndNoDate()
        {
            var roster = new List<KerbalsPresentation.RosterKerbal>
                { Kerbal("Jebediah Kerman", "Pilot") };
            var res = new Dictionary<string, KerbalsModule.KerbalReservation>(
                StringComparer.Ordinal)
            {
                { "Jebediah Kerman", Res("Jebediah Kerman", 18230.0) },
            };
            var entries = new List<KerbalsWindowUI.CrewEndStateEntry>
            {
                Entry("Jebediah Kerman", "r1", "Jumping Flea", 18230.0,
                    KerbalEndState.Recovered, startUT: 17000.0),
            };

            KerbalsPresentation.RosterRowSet set = Roster(
                roster,
                new Dictionary<string, KerbalsModule.KerbalSlot>(StringComparer.Ordinal)
                {
                    { "Jebediah Kerman",
                        Slot("Jebediah Kerman", "Pilot", new List<string>()) },
                },
                res,
                flights: Flights(entries));

            KerbalsPresentation.RosterRow jeb = Find(set, "Jebediah Kerman");
            // His own name is not repeated ("Reserved for Jebediah Kerman" on his own row).
            Assert.Equal("Reserved: Jumping Flea", jeb.StatusText);
            Assert.DoesNotContain("until", jeb.StatusText);
            Assert.DoesNotContain("D18230", jeb.StatusText);
            Assert.Equal(
                "Held by the committed flight Jumping Flea, which ends with this kerbal "
                + "recovered. " + KerbalsPresentation.ReservationHoldRule,
                jeb.StatusTooltipText);
        }

        [Fact]
        public void Roster_AnOpenEndedReservationNamesTheVesselTheKerbalIsAboard()
        {
            var roster = new List<KerbalsPresentation.RosterKerbal>
                { Kerbal("Bill Kerman", "Engineer") };
            var res = new Dictionary<string, KerbalsModule.KerbalReservation>(
                StringComparer.Ordinal)
            {
                { "Bill Kerman", Res("Bill Kerman", double.PositiveInfinity) },
            };
            var entries = new List<KerbalsWindowUI.CrewEndStateEntry>
            {
                Entry("Bill Kerman", "r1", "Kerbal X #2", 900.0, KerbalEndState.Aboard),
            };

            KerbalsPresentation.RosterRowSet set =
                Roster(roster, reservations: res, flights: Flights(entries));

            KerbalsPresentation.RosterRow bill = Find(set, "Bill Kerman");
            Assert.Equal("Reserved: aboard Kerbal X #2", bill.StatusText);
            Assert.Equal(
                "Held by the committed flight Kerbal X #2, which ends with this kerbal "
                + "aboard Kerbal X #2. " + KerbalsPresentation.ReservationHoldRule,
                bill.StatusTooltipText);
        }

        [Fact]
        public void Roster_AReservationWithNoFlightOfItsOwnStillCarriesTheReleaseRule()
        {
            var roster = new List<KerbalsPresentation.RosterKerbal>
                { Kerbal("Jebediah Kerman", "Pilot") };
            var res = new Dictionary<string, KerbalsModule.KerbalReservation>(
                StringComparer.Ordinal)
            {
                { "Jebediah Kerman", Res("Jebediah Kerman", double.PositiveInfinity) },
            };

            KerbalsPresentation.RosterRowSet set = Roster(roster, reservations: res);

            KerbalsPresentation.RosterRow jeb = Find(set, "Jebediah Kerman");
            Assert.Equal("Reserved", jeb.StatusText);
            Assert.Equal("Held by a committed flight. "
                         + KerbalsPresentation.ReservationHoldRule,
                jeb.StatusTooltipText);
        }

        [Fact]
        public void Roster_ALongVesselNameFallsBackToAShortFormAndStaysInTheHover()
        {
            var roster = new List<KerbalsPresentation.RosterKerbal>
                { Kerbal("Bill Kerman", "Engineer") };
            var res = new Dictionary<string, KerbalsModule.KerbalReservation>(
                StringComparer.Ordinal)
            {
                { "Bill Kerman", Res("Bill Kerman", double.PositiveInfinity) },
            };
            const string longName = "Duna Surface Sample Return Ascent Stage";
            var entries = new List<KerbalsWindowUI.CrewEndStateEntry>
            {
                Entry("Bill Kerman", "r1", longName, 900.0, KerbalEndState.Aboard),
            };

            KerbalsPresentation.RosterRow bill = Find(
                Roster(roster, reservations: res, flights: Flights(entries)), "Bill Kerman");

            Assert.Equal("Reserved: still aboard", bill.StatusText);
            Assert.Contains(longName, bill.StatusTooltipText);
        }

        [Fact]
        public void Roster_AReservedStandInNamesTheSlotItServes()
        {
            var roster = new List<KerbalsPresentation.RosterKerbal>
            {
                Kerbal("Jebediah Kerman", "Pilot"),
                Kerbal("Lars Kerman", "Pilot"),
            };
            var res = new Dictionary<string, KerbalsModule.KerbalReservation>(
                StringComparer.Ordinal)
            {
                { "Lars Kerman", Res("Lars Kerman", 4200.0) },
            };

            KerbalsPresentation.RosterRowSet set = Roster(roster, JebCoveredByLars(), res);

            KerbalsPresentation.RosterRow lars = Find(set, "Lars Kerman");
            Assert.Equal("Reserved for Jebediah Kerman", lars.StatusText);
            Assert.Equal("Held by a committed flight flown in Jebediah Kerman's seat. "
                         + KerbalsPresentation.ReservationHoldRule,
                lars.StatusTooltipText);
        }

        [Fact]
        public void Roster_AStandInGetsItsOwnRowNamingTheOwner()
        {
            var roster = new List<KerbalsPresentation.RosterKerbal>
            {
                Kerbal("Jebediah Kerman", "Pilot"),
                Kerbal("Lars Kerman", "Pilot"),
            };
            // Jeb has to be AWAY for Lars to be the active occupant: chain MEMBERSHIP is
            // not what makes a kerbal the stand-in (see ClassifyStatus).
            KerbalsPresentation.RosterRowSet set = Roster(
                roster, JebCoveredByLars(), JebReserved());

            KerbalsPresentation.RosterRow lars = Find(set, "Lars Kerman");
            Assert.Equal(KerbalsPresentation.RosterStatus.StandIn, lars.Status);
            Assert.Equal("Stand-in for Jebediah Kerman", lars.StatusText);
            Assert.Equal("Jebediah Kerman", lars.SlotOwnerName);
            Assert.Contains("Lars Kerman", set.Involved.Select(r => r.Name));
        }

        [Fact]
        public void Roster_AParsekStandInMissingFromTheStockRosterStillGetsARow()
        {
            // The stock list can have dropped a Parsek-created stand-in; the ledger's own
            // names are added so the row does not silently vanish.
            var roster = new List<KerbalsPresentation.RosterKerbal>
                { Kerbal("Jebediah Kerman", "Pilot") };

            KerbalsPresentation.RosterRowSet set = Roster(
                roster, JebCoveredByLars(), JebReserved());

            Assert.Equal("Stand-in for Jebediah Kerman",
                Find(set, "Lars Kerman").StatusText);
        }

        [Fact]
        public void Roster_ARetireeWithNoSlotStillGetsARow()
        {
            KerbalsPresentation.RosterRowSet set = Roster(
                new List<KerbalsPresentation.RosterKerbal>(),
                retired: new List<string> { "Hanley Kerman" });

            KerbalsPresentation.RosterRow row = Find(set, "Hanley Kerman");
            Assert.Equal(KerbalsPresentation.RosterStatus.Retired, row.Status);
            Assert.Equal("Retired", row.StatusText);
            Assert.Null(row.StatusTooltipText);
        }

        [Fact]
        public void Roster_RetiredWinsOverStandIn()
        {
            var roster = new List<KerbalsPresentation.RosterKerbal>
                { Kerbal("Lars Kerman", "Pilot") };

            KerbalsPresentation.RosterRowSet set = Roster(
                roster, JebCoveredByLars(), retired: new List<string> { "Lars Kerman" });

            Assert.Equal("Retired", Find(set, "Lars Kerman").StatusText);
        }

        [Fact]
        public void Roster_ADeceasedOwnerReadsLostAndItsHoverNamesTheMissionAndTheWayBack()
        {
            var roster = new List<KerbalsPresentation.RosterKerbal>
                { Kerbal("Bill Kerman", "Engineer") };
            var slots = new Dictionary<string, KerbalsModule.KerbalSlot>(StringComparer.Ordinal)
            {
                { "Bill Kerman", Slot("Bill Kerman", "Engineer",
                    new List<string> { "Jane Kerman" }, permanentlyGone: true) },
            };
            var entries = new List<KerbalsWindowUI.CrewEndStateEntry>
            {
                Entry("Bill Kerman", "r1", "Mun Hopper", 100.0, KerbalEndState.Recovered),
                Entry("Bill Kerman", "r2", "Last Ride", 900.0, KerbalEndState.Dead,
                    startUT: 700.0),
            };

            KerbalsPresentation.RosterRowSet set =
                Roster(roster, slots, flights: Flights(entries));

            KerbalsPresentation.RosterRow row = Find(set, "Bill Kerman");
            Assert.Equal(KerbalsPresentation.RosterStatus.Lost, row.Status);
            Assert.Equal("Lost", row.StatusText);
            // Dated by the mission's LAUNCH, like the Flights tab's Date column.
            Assert.Equal("Lost on Last Ride (launched D700). "
                         + KerbalsPresentation.LostReFlyRemedy,
                row.StatusTooltipText);
            Assert.Equal("Last Ride - Lost", row.LastFlightText);
            // The Last flight cell is the Timeline cross-link to that mission.
            Assert.Equal("r2", row.LastFlightRecordingId);
        }

        // catches: the c1 capture's phantom "Lars Kerman / Available" row. Jane is the
        // dead owner's displaced chain member, not in the stock roster, not retired, not
        // reserved - exactly the stand-in KerbalsModule.ApplyToRoster deletes - so she is
        // no row at all.
        [Fact]
        public void Roster_ADeletedStandInIsNotListedAsAvailable()
        {
            var roster = new List<KerbalsPresentation.RosterKerbal>
                { Kerbal("Bill Kerman", "Engineer") };
            var slots = new Dictionary<string, KerbalsModule.KerbalSlot>(StringComparer.Ordinal)
            {
                { "Bill Kerman", Slot("Bill Kerman", "Engineer",
                    new List<string> { "Jane Kerman" }, permanentlyGone: true) },
            };

            KerbalsPresentation.RosterRowSet set = Roster(roster, slots);

            Assert.DoesNotContain("Jane Kerman", set.Involved.Select(r => r.Name));
            Assert.DoesNotContain("Jane Kerman", set.Plain.Select(r => r.Name));
            Assert.Equal(1, set.OmittedStandIns);
            Assert.Equal(0, Find(set, "Bill Kerman").SlotMemberCount);
        }

        [Fact]
        public void Roster_ADisplacedStandInStillInTheStockRosterStaysListed()
        {
            // Same slot, but the stock list still carries Jane (she is seated on a live
            // vessel, say): she exists, so she is listed under the owner.
            var roster = new List<KerbalsPresentation.RosterKerbal>
            {
                Kerbal("Bill Kerman", "Engineer"),
                Kerbal("Jane Kerman", "Engineer", "Rover"),
            };
            var slots = new Dictionary<string, KerbalsModule.KerbalSlot>(StringComparer.Ordinal)
            {
                { "Bill Kerman", Slot("Bill Kerman", "Engineer",
                    new List<string> { "Jane Kerman" }, permanentlyGone: true) },
            };

            KerbalsPresentation.RosterRowSet set = Roster(roster, slots);

            KerbalsPresentation.RosterRow jane = Find(set, "Jane Kerman");
            Assert.Equal("Assigned (Rover)", jane.StatusText);
            Assert.Equal(1, jane.Depth);
            Assert.Equal(0, set.OmittedStandIns);
        }

        [Theory]
        [InlineData((int)KerbalsWindowUI.ChainMemberStatus.Displaced, false, false, false, true)]
        [InlineData((int)KerbalsWindowUI.ChainMemberStatus.Displaced, true, false, false, false)]
        [InlineData((int)KerbalsWindowUI.ChainMemberStatus.Displaced, false, true, false, false)]
        [InlineData((int)KerbalsWindowUI.ChainMemberStatus.Displaced, false, false, true, false)]
        [InlineData((int)KerbalsWindowUI.ChainMemberStatus.Active, false, false, false, false)]
        [InlineData((int)KerbalsWindowUI.ChainMemberStatus.Retired, false, false, false, false)]
        public void IsDeletedStandIn_OnlyADisplacedUnlistedUnretiredUnreservedMember(
            int status, bool inRoster, bool retired, bool reserved, bool expected)
        {
            Assert.Equal(expected, KerbalsPresentation.IsDeletedStandIn(
                (KerbalsWindowUI.ChainMemberStatus)status, inRoster, retired, reserved));
        }

        [Fact]
        public void Roster_APermanentReservationAlsoReadsLost()
        {
            var roster = new List<KerbalsPresentation.RosterKerbal>
                { Kerbal("Bill Kerman", "Engineer") };
            var res = new Dictionary<string, KerbalsModule.KerbalReservation>(
                StringComparer.Ordinal)
            {
                { "Bill Kerman",
                    Res("Bill Kerman", double.PositiveInfinity, permanent: true) },
            };

            KerbalsPresentation.RosterRowSet set = Roster(roster, reservations: res);

            Assert.Equal("Lost", Find(set, "Bill Kerman").StatusText);
        }

        // catches: an open-ended hold named after the kerbal's LATEST mission when that
        // one ended recovered - the hold comes from the mission that left him aboard.
        [Fact]
        public void Roster_AnOpenEndedHoldNamesTheLatestAboardMissionOverALaterRecoveredOne()
        {
            var roster = new List<KerbalsPresentation.RosterKerbal>
                { Kerbal("Jebediah Kerman", "Pilot") };
            var res = new Dictionary<string, KerbalsModule.KerbalReservation>(
                StringComparer.Ordinal)
            {
                { "Jebediah Kerman", Res("Jebediah Kerman", double.PositiveInfinity) },
            };
            var entries = new List<KerbalsWindowUI.CrewEndStateEntry>
            {
                Entry("Jebediah Kerman", "r1", "Mun Hopper", 1200.0, KerbalEndState.Aboard),
                Entry("Jebediah Kerman", "r2", "Minmus Hop", 1500.0, KerbalEndState.Recovered),
            };

            KerbalsPresentation.RosterRowSet set =
                Roster(roster, reservations: res, flights: Flights(entries));

            KerbalsPresentation.RosterRow jeb = Find(set, "Jebediah Kerman");
            Assert.Equal("Reserved: aboard Mun Hopper", jeb.StatusText);
            // The Last flight cell still reads the latest-ENDING mission.
            Assert.Equal("Minmus Hop - Recovered", jeb.LastFlightText);
            Assert.Equal("r2", jeb.LastFlightRecordingId);
        }

        [Fact]
        public void ResolveHoldFlight_NoGroupOrNoRowsIsNull()
        {
            Assert.Null(KerbalsPresentation.ResolveHoldFlight(
                Res("Jebediah Kerman", 5.0), null));
            Assert.Null(KerbalsPresentation.ResolveHoldFlight(
                Res("Jebediah Kerman", 5.0),
                new KerbalsPresentation.FlightGroup
                    { Rows = new List<KerbalsPresentation.FlightRow>() }));
        }

        [Fact]
        public void ResolveHoldFlight_AFiniteHoldIsTheLatestEndingMission()
        {
            var group = new KerbalsPresentation.FlightGroup
            {
                Rows = new List<KerbalsPresentation.FlightRow>
                {
                    new KerbalsPresentation.FlightRow
                        { MissionText = "A", EndUT = 100, EndState = KerbalEndState.Aboard },
                    new KerbalsPresentation.FlightRow
                        { MissionText = "B", EndUT = 200, EndState = KerbalEndState.Recovered },
                }
            };
            Assert.Equal("B", KerbalsPresentation.ResolveHoldFlight(
                Res("Jebediah Kerman", 200.0), group).Value.MissionText);
            Assert.Equal("A", KerbalsPresentation.ResolveHoldFlight(
                Res("Jebediah Kerman", double.PositiveInfinity), group).Value.MissionText);
        }

        [Fact]
        public void Roster_AStandInRowKeepsItsLastFlight()
        {
            var roster = new List<KerbalsPresentation.RosterKerbal>
            {
                Kerbal("Jebediah Kerman", "Pilot"),
                Kerbal("Lars Kerman", "Pilot"),
            };
            var entries = new List<KerbalsWindowUI.CrewEndStateEntry>
            {
                Entry("Lars Kerman", "r1", "Mun Hopper", 1200.0, KerbalEndState.Recovered),
            };

            KerbalsPresentation.RosterRowSet set =
                Roster(roster, JebCoveredByLars(), flights: Flights(entries));

            KerbalsPresentation.RosterRow lars = Find(set, "Lars Kerman");
            Assert.Equal("Mun Hopper - Recovered", lars.LastFlightText);
        }

        [Fact]
        public void Roster_RowsAreOrderedOrdinallyByName()
        {
            var roster = new List<KerbalsPresentation.RosterKerbal>
            {
                Kerbal("Valentina Kerman", "Pilot", "Station"),
                Kerbal("Bill Kerman", "Engineer", "Station"),
                Kerbal("Bob Kerman", "Scientist", "Station"),
            };

            KerbalsPresentation.RosterRowSet set = Roster(roster);

            Assert.Equal(new[] { "Bill Kerman", "Bob Kerman", "Valentina Kerman" },
                set.Involved.Select(r => r.Name).ToArray());
        }

        // catches: a stand-in sorting alphabetically away from the kerbal he covers
        // (the pre-2026-09-22 roster drew Jane three rows from Bill and repeated her
        // inside a chain fold on Bill's row). Rows are grouped BY SLOT now.
        [Fact]
        public void Roster_StandInsAreListedDirectlyUnderTheOwnerTheyCover()
        {
            var roster = new List<KerbalsPresentation.RosterKerbal>
            {
                Kerbal("Bill Kerman", "Engineer"),
                Kerbal("Jebediah Kerman", "Pilot", "Station"),
                Kerbal("Zed Kerman", "Engineer"),
            };
            var slots = new Dictionary<string, KerbalsModule.KerbalSlot>(StringComparer.Ordinal)
            {
                { "Bill Kerman",
                    Slot("Bill Kerman", "Engineer", new List<string> { "Zed Kerman" }) },
            };
            var res = new Dictionary<string, KerbalsModule.KerbalReservation>(
                StringComparer.Ordinal)
            {
                { "Bill Kerman", Res("Bill Kerman", double.PositiveInfinity) },
            };

            KerbalsPresentation.RosterRowSet set = Roster(roster, slots, res);

            Assert.Equal(new[] { "Bill Kerman", "Zed Kerman", "Jebediah Kerman" },
                set.Involved.Select(r => r.Name).ToArray());
            KerbalsPresentation.RosterRow bill = set.Involved[0];
            KerbalsPresentation.RosterRow zed = set.Involved[1];
            Assert.Equal(0, bill.Depth);
            Assert.Equal(1, bill.SlotMemberCount);
            Assert.Equal(1, zed.Depth);
            Assert.True(zed.IsLastInSlot);
            Assert.Equal(KerbalsWindowUI.ChainMemberStatus.Active, zed.MemberStatus);
            Assert.Equal("Stand-in for Bill Kerman", zed.StatusText);
            Assert.Equal("Bill Kerman", zed.SlotOwnerName);
            Assert.Null(set.Involved[2].MemberStatus);
        }

        [Fact]
        public void Roster_ATwoDeepChainListsBothMembersInChainOrderWithTheMidGlyphFirst()
        {
            var roster = new List<KerbalsPresentation.RosterKerbal>
            {
                Kerbal("Jebediah Kerman", "Pilot"),
                Kerbal("Mies Kerman", "Engineer", "Ike 1"),
                Kerbal("Lars Kerman", "Pilot"),
            };
            var res = new Dictionary<string, KerbalsModule.KerbalReservation>(
                StringComparer.Ordinal)
            {
                { "Jebediah Kerman", Res("Jebediah Kerman", double.PositiveInfinity) },
                { "Lars Kerman", Res("Lars Kerman", double.PositiveInfinity) },
            };

            KerbalsPresentation.RosterRowSet set = Roster(roster, JebCoveredByThree(), res);

            Assert.Equal(new[] { "Jebediah Kerman", "Lars Kerman", "Mies Kerman" },
                set.Involved.Select(r => r.Name).ToArray());
            Assert.False(set.Involved[1].IsLastInSlot);
            Assert.True(set.Involved[2].IsLastInSlot);
            Assert.Equal("Reserved for Jebediah Kerman", set.Involved[1].StatusText);
            // "Stand-in for Jebediah Kerman (aboard Ike 1)" overflows the 31-char cell, so
            // the vessel moves to the hover text.
            Assert.Equal("Stand-in for Jebediah Kerman", set.Involved[2].StatusText);
            Assert.Equal("Standing in for Jebediah Kerman; aboard Ike 1.",
                set.Involved[2].StatusTooltipText);
            Assert.Equal(2, set.Involved[0].SlotMemberCount);
        }

        [Fact]
        public void Roster_ARetiredChainMemberIsTaggedRetiredRatherThanActive()
        {
            var roster = new List<KerbalsPresentation.RosterKerbal>
                { Kerbal("Jebediah Kerman", "Pilot") };

            KerbalsPresentation.RosterRowSet set = Roster(
                roster, JebCoveredByLars(), retired: new List<string> { "Lars Kerman" });

            KerbalsPresentation.RosterRow lars = Find(set, "Lars Kerman");
            Assert.Equal(KerbalsWindowUI.ChainMemberStatus.Retired, lars.MemberStatus);
            Assert.Equal(1, lars.Depth);
            Assert.Equal("Retired", lars.StatusText);
        }

        [Fact]
        public void Roster_IsPlainRowIsExactlyAvailableWithNoSlotAndNoFlights()
        {
            var plain = new KerbalsPresentation.RosterRow
            {
                Name = "Bob Kerman",
                Status = KerbalsPresentation.RosterStatus.Available,
            };
            Assert.True(KerbalsPresentation.IsPlainRow(plain));

            var withFlight = plain;
            withFlight.HasFlights = true;
            Assert.False(KerbalsPresentation.IsPlainRow(withFlight));

            var withSlot = plain;
            withSlot.SlotOwnerName = "Jebediah Kerman";
            Assert.False(KerbalsPresentation.IsPlainRow(withSlot));

            var retiredRow = plain;
            retiredRow.Status = KerbalsPresentation.RosterStatus.Retired;
            Assert.False(KerbalsPresentation.IsPlainRow(retiredRow));
        }

        // ------------------------------------------------------------------
        // Window-side formatters
        // ------------------------------------------------------------------

        [Fact]
        public void FormatRosterNameCell_TopLevelIsTheNameAndNestedRowsCarryTheTreeGlyph()
        {
            var row = new KerbalsPresentation.RosterRow
                { Name = "Bill Kerman", Trait = "Engineer" };
            Assert.Equal("Bill Kerman [Engineer]", KerbalsWindowUI.FormatRosterNameCell(row));

            var noTrait = new KerbalsPresentation.RosterRow { Name = "Lars Kerman" };
            Assert.Equal("Lars Kerman", KerbalsWindowUI.FormatRosterNameCell(noTrait));

            var mid = new KerbalsPresentation.RosterRow
                { Name = "Lars Kerman", Trait = "Pilot", Depth = 1, IsLastInSlot = false };
            Assert.Equal("\u251c\u2500 Lars Kerman [Pilot]",
                KerbalsWindowUI.FormatRosterNameCell(mid));

            var last = mid;
            last.IsLastInSlot = true;
            Assert.Equal("\u2514\u2500 Lars Kerman [Pilot]",
                KerbalsWindowUI.FormatRosterNameCell(last));

            var deeper = last;
            deeper.Depth = 2;
            Assert.Equal(KerbalsWindowUI.SubitemIndent + "\u2514\u2500 Lars Kerman [Pilot]",
                KerbalsWindowUI.FormatRosterNameCell(deeper));
        }

        [Fact]
        public void SubitemIndent_IsTheWidthOfTheTreeGlyph()
        {
            Assert.Equal("   ", KerbalsWindowUI.SubitemIndent);
        }

        [Fact]
        public void TooltipForOutcome_UnknownSaysTheFlightHasNoRecordedEnding()
        {
            Assert.Equal("The flight has no recorded ending.",
                KerbalsWindowUI.TooltipForOutcome(KerbalEndState.Unknown));
            Assert.Contains("recovered",
                KerbalsWindowUI.TooltipForOutcome(KerbalEndState.Recovered));
        }

        [Fact]
        public void DescribeFlightRow_CarriesTheRawRecordingIdentityForTheHoverStrip()
        {
            var row = new KerbalsPresentation.FlightRow
            {
                RecordingId = "rec-42",
                RecordingName = "Mun Hopper",
            };
            Assert.Equal("Recorded flight 'Mun Hopper' (id rec-42).",
                KerbalsWindowUI.DescribeFlightRow(row));

            var unnamed = new KerbalsPresentation.FlightRow { RecordingId = "rec-9" };
            Assert.Equal("Recorded flight '(unnamed)' (id rec-9).",
                KerbalsWindowUI.DescribeFlightRow(unnamed));
        }

        [Fact]
        public void FormatRowDate_IsTheTimelinesOwnCompactCalendarForm()
        {
            // Measured, not assumed: KSPUtil.PrintDateCompact DOES run in the headless
            // host, so this is the same string the Timeline's row labels carry for the
            // same UT - which is the point of routing both through one formatter. The
            // try/catch fallback behind it is exercised where it can be: the builders'
            // own no-formatter path (Flights_DateCell_FallsBackToInvariantRawUtWithNo-
            // Formatter) pins the invariant raw-UT spelling.
            Assert.Equal(
                TimelineWindowUI.FormatTimelineEntryTimeLabel(12045.6, 0.0, false),
                KerbalsWindowUI.FormatRowDate(12045.6));
        }

        // ------------------------------------------------------------------
        // Culture
        // ------------------------------------------------------------------

        // catches: a row builder picking up the OS culture. Every numeric cell in this
        // window goes through an InvariantCulture format (or through the injected date
        // formatter), so a de-DE host must produce byte-identical rows. Today's formats are
        // separator-free integers, so this cell is a FLOOR rather than a proof - it reds
        // the moment a site starts spelling a decimal or a grouped number without pinning
        // the culture.
        [Fact]
        public void BothBuildersAreCultureInvariant()
        {
            var roster = new List<KerbalsPresentation.RosterKerbal>
                { Kerbal("Jebediah Kerman", "Pilot") };
            var res = new Dictionary<string, KerbalsModule.KerbalReservation>(
                StringComparer.Ordinal)
            {
                { "Jebediah Kerman", Res("Jebediah Kerman", 1234567.89) },
            };
            var entries = new List<KerbalsWindowUI.CrewEndStateEntry>
            {
                Entry("Jebediah Kerman", "r1", "Mun Hopper", 1234567.89,
                    KerbalEndState.Recovered),
            };

            Func<string[]> render = () =>
            {
                // No date formatter on either builder, so every cell below is one of the
                // window's OWN numeric formats rather than the injected calendar's.
                List<KerbalsPresentation.FlightGroup> flights =
                    KerbalsPresentation.BuildFlightRows(
                        entries, null, null, null, null, null, null);
                KerbalsPresentation.RosterRowSet set = KerbalsPresentation.BuildRosterRows(
                    roster, null, res, null, flights, ActiveChainIndexLike(res), null);
                return new[]
                {
                    flights[0].HeaderText,
                    flights[0].Rows[0].DateText,
                    set.Involved[0].StatusText,
                    set.Involved[0].StatusTooltipText,
                    set.Involved[0].LastFlightText,
                };
            };

            var previous = Thread.CurrentThread.CurrentCulture;
            string[] invariant;
            string[] german;
            try
            {
                Thread.CurrentThread.CurrentCulture = CultureInfo.InvariantCulture;
                invariant = render();
                Thread.CurrentThread.CurrentCulture = new CultureInfo("de-DE");
                german = render();
            }
            finally
            {
                Thread.CurrentThread.CurrentCulture = previous;
            }

            Assert.Equal(invariant, german);
            Assert.Equal("1234568", german[1]);
            Assert.Equal("Reserved: Mun Hopper", german[2]);
        }

        // ------------------------------------------------------------------
        // Fold / expand state, including the op=expand seam accessors
        // ------------------------------------------------------------------

        private static KerbalsWindowUI.KerbalsViewModel SeededVM()
        {
            var roster = new List<KerbalsPresentation.RosterKerbal>
            {
                Kerbal("Jebediah Kerman", "Pilot"),
                Kerbal("Bob Kerman", "Scientist"),
            };
            var recs = new List<Recording>
            {
                RecWithEndStates("r1", "Mun Hopper", 100.0,
                    new Dictionary<string, KerbalEndState>
                        { { "Jebediah Kerman", KerbalEndState.Recovered } }),
            };
            var res = new Dictionary<string, KerbalsModule.KerbalReservation>(
                StringComparer.Ordinal)
            {
                { "Jebediah Kerman", Res("Jebediah Kerman", 500.0) },
            };
            return KerbalsWindowUI.BuildViewModel(
                roster, JebCoveredByLars(), res, null, recs,
                null, null, null, ActiveChainIndexLike(res), FakeDate);
        }

        [Fact]
        public void ExpandSeam_EnumeratesOnlyTheKeysTheWindowDraws()
        {
            var ui = new KerbalsWindowUI(null);
            // Before the first drawn frame there is no view model, so no key is offered -
            // which is why op=expand settles on a frame.
            Assert.Empty(ui.EnumerateRosterExpandKeysForTesting());
            Assert.Empty(ui.EnumerateFlightExpandKeysForTesting());

            ui.CachedViewModelForTesting = SeededVM();

            // The plain-kerbal fold row is the ONE Roster key left: since the slot
            // grouping, a stand-in is always drawn under his owner, so no owner row folds.
            Assert.Equal(new[] { KerbalsWindowUI.PlainBucketKey },
                ui.EnumerateRosterExpandKeysForTesting().ToArray());

            Assert.Equal(new[] { "Jebediah Kerman" },
                ui.EnumerateFlightExpandKeysForTesting().ToArray());
        }

        [Fact]
        public void ExpandSeam_TheRosterKeyWritesThePlainBucketOnly()
        {
            var ui = new KerbalsWindowUI(null);
            ui.CachedViewModelForTesting = SeededVM();

            Assert.Equal(0, ui.ExpandedRosterCountForTesting);
            // A kerbal name is no Roster key any more: nothing changes.
            Assert.False(ui.SetRosterExpandedForTesting("Jebediah Kerman", true));
            Assert.True(ui.SetRosterExpandedForTesting(KerbalsWindowUI.PlainBucketKey, true));
            // Idempotent: a second write of the same state changes nothing, which is what
            // the op's changed= term reports.
            Assert.False(ui.SetRosterExpandedForTesting(KerbalsWindowUI.PlainBucketKey, true));
            Assert.Equal(1, ui.ExpandedRosterCountForTesting);

            Assert.True(ui.SetRosterExpandedForTesting(KerbalsWindowUI.PlainBucketKey, false));
            Assert.Equal(0, ui.ExpandedRosterCountForTesting);
        }

        [Fact]
        public void ExpandSeam_FlightKeysAreInvertedAgainstTheFoldedSet()
        {
            var ui = new KerbalsWindowUI(null);
            ui.CachedViewModelForTesting = SeededVM();

            // Default-unfolded: the one group is already expanded.
            Assert.Equal(1, ui.ExpandedFlightCountForTesting);
            Assert.True(ui.SetFlightExpandedForTesting("Jebediah Kerman", false));
            Assert.Contains("Jebediah Kerman", ui.foldedKerbals);
            Assert.Equal(0, ui.ExpandedFlightCountForTesting);
            Assert.True(ui.SetFlightExpandedForTesting("Jebediah Kerman", true));
            Assert.Equal(1, ui.ExpandedFlightCountForTesting);
        }

        [Fact]
        public void ToggleFold_WhenNotFolded_AddsToSetAndLogsFolded()
        {
            var folded = new HashSet<string>(StringComparer.Ordinal);

            bool nowFolded = KerbalsWindowUI.ToggleFold(folded, "Bill Kerman", 3);

            Assert.True(nowFolded);
            Assert.Contains("Bill Kerman", folded);
            Assert.Contains(logLines, l =>
                l.Contains("[UI]")
                && l.Contains("Kerbals fold toggled")
                && l.Contains("'Bill Kerman'")
                && l.Contains("-> folded")
                && l.Contains("(3 missions)"));
        }

        [Fact]
        public void ToggleFold_WhenAlreadyFolded_RemovesFromSetAndLogsUnfolded()
        {
            var folded = new HashSet<string>(StringComparer.Ordinal) { "Jebediah Kerman" };

            bool nowFolded = KerbalsWindowUI.ToggleFold(folded, "Jebediah Kerman", 5);

            Assert.False(nowFolded);
            Assert.DoesNotContain("Jebediah Kerman", folded);
            Assert.Contains(logLines, l =>
                l.Contains("[UI]")
                && l.Contains("Kerbals fold toggled")
                && l.Contains("'Jebediah Kerman'")
                && l.Contains("-> unfolded")
                && l.Contains("(5 missions)"));
        }

        [Fact]
        public void InvalidateCache_DoesNotClearFoldedKerbals()
        {
            // Fold state is transient UI preference - it must survive cachedVM invalidation
            // (e.g. after a ledger recalc) so the player does not silently lose their fold
            // choices mid-session.
            var ui = new KerbalsWindowUI(null);
            ui.foldedKerbals.Add("Bill Kerman");
            ui.foldedKerbals.Add("Jebediah Kerman");

            ui.InvalidateCache();

            Assert.Contains("Bill Kerman", ui.foldedKerbals);
            Assert.Contains("Jebediah Kerman", ui.foldedKerbals);
        }

        // ------------------------------------------------------------------
        // The Timeline cross-link
        // ------------------------------------------------------------------

        [Fact]
        public void OnFatesRowClicked_InvokesCallbackWithRecordingId()
        {
            string captured = null;
            KerbalsWindowUI.OnFatesRowClicked(id => captured = id, "rec-42");
            Assert.Equal("rec-42", captured);
        }

        [Fact]
        public void OnFatesRowClicked_LogsRecordingId()
        {
            KerbalsWindowUI.OnFatesRowClicked(_ => { }, "rec-42");
            Assert.Contains(logLines, l =>
                l.Contains("[UI]")
                && l.Contains("Kerbals Fates \u2192 Timeline scroll")
                && l.Contains("recordingId=rec-42"));
        }

        [Fact]
        public void OnFatesRowClicked_NullCallback_NoOpAndLogsOnce()
        {
            // Regression: E14 - the pure helper must not NRE when the production callback
            // (GetTimelineUI().ScrollToRecording) is missing, e.g. during a cold-start
            // scene transition.
            var ex = Record.Exception(() =>
                KerbalsWindowUI.OnFatesRowClicked(null, "rec-stale"));
            Assert.Null(ex);
            int matches = logLines.Count(l =>
                l.Contains("[UI]")
                && l.Contains("Kerbals Fates \u2192 Timeline scroll")
                && l.Contains("recordingId=rec-stale"));
            Assert.Equal(1, matches);
        }

        // catches: the Flights row's tooltip promise ("Scrolls the Timeline window to the
        // flight this row came from") going back to doing nothing visible with the Timeline
        // closed. The pending id is consumed inside the Timeline's DRAW path, so storing it
        // against a shut window scrolls nothing now and then jumps the list whenever the
        // player next opens it for something else (finding P6).
        [Fact]
        public void ScrollToRecording_OpensTheTimelineWindowWhenItIsClosed()
        {
            var ui = new ParsekUI(UIMode.KSC);
            try
            {
                TimelineWindowUI timeline = ui.GetTimelineUI();
                Assert.False(timeline.IsOpen);

                // Exactly what a Flights row hands OnFatesRowClicked.
                KerbalsWindowUI.OnFatesRowClicked(timeline.ScrollToRecording, "rec-fate-7");

                Assert.True(timeline.IsOpen);
                Assert.Contains(logLines, l =>
                    l.Contains("[Timeline]")
                    && l.Contains("recordingId=rec-fate-7")
                    && l.Contains("windowWasOpen=False"));
            }
            finally
            {
                // ParsekUI.Cleanup resets cached Unity GUI styles, which headless xUnit
                // cannot do. Only that pair is swallowed (precedent:
                // UiComplexityModeCloseHandlerTests.CleanupIgnoringUnityTeardown); a bare
                // catch would also hide a real failure in the assertions' own teardown.
                try { ui.Cleanup(); }
                catch (System.Security.SecurityException) { }
                catch (MissingMethodException) { }
            }
        }

        // ------------------------------------------------------------------
        // The composed view model
        // ------------------------------------------------------------------

        [Fact]
        public void BuildViewModel_ComposesBothTabsAndLogsTheirCounts()
        {
            KerbalsWindowUI.KerbalsViewModel vm = SeededVM();

            Assert.Single(vm.EndStates);
            Assert.Single(vm.Flights);
            // Jeb (reserved) and Lars (stand-in) are listed; Bob is plain.
            Assert.Equal(2, vm.Roster.Involved.Count);
            Assert.Single(vm.Roster.Plain);
            Assert.Contains(logLines, l =>
                l.Contains("[UI]")
                && l.Contains("KerbalsWindow: built VM")
                && l.Contains("roster=2+1")
                && l.Contains("omittedStandIns=0")
                && l.Contains("flightGroups=1")
                && l.Contains("endStates=1"));
        }

        [Fact]
        public void BuildViewModel_TheLiveRosterTraitWinsOverTheSlotsStoredCopy()
        {
            var roster = new List<KerbalsPresentation.RosterKerbal>
                { Kerbal("Jebediah Kerman", "Engineer") };
            var slots = new Dictionary<string, KerbalsModule.KerbalSlot>(StringComparer.Ordinal)
            {
                { "Jebediah Kerman",
                    Slot("Jebediah Kerman", "Pilot", new List<string>()) },
            };
            var recs = new List<Recording>
            {
                RecWithEndStates("r1", "Mun Hopper", 100.0,
                    new Dictionary<string, KerbalEndState>
                        { { "Jebediah Kerman", KerbalEndState.Recovered } }),
            };
            var empty = new Dictionary<string, KerbalsModule.KerbalReservation>(
                StringComparer.Ordinal);

            KerbalsWindowUI.KerbalsViewModel vm = KerbalsWindowUI.BuildViewModel(
                roster, slots, empty, null, recs, null, null, null,
                ActiveChainIndexLike(empty), FakeDate);

            Assert.Contains("[Engineer]", vm.Flights[0].HeaderText);
        }

        // ------------------------------------------------------------------
        // S2 - the row unit is the MISSION, not the recorded segment
        // ------------------------------------------------------------------

        // catches: the defect the GUI-11 capture read - five rows per kerbal for what the
        // player did as one mission, all but one of them repeating the same date and
        // mission name, and one of them "Outcome unknown" for a mid-mission segment.
        [Fact]
        public void Missions_AThreeSegmentTreeCollapsesToOneRowWithTheFinalOutcome()
        {
            var entries = new List<KerbalsWindowUI.CrewEndStateEntry>
            {
                Entry("Bill Kerman", "seg1", "Kerbal X", 100.0, KerbalEndState.Aboard,
                    treeId: "T1", startUT: 10.0),
                // The middle segment has no recorded ending. It must NOT be the answer.
                Entry("Bill Kerman", "seg2", "Kerbal X", 200.0, KerbalEndState.Unknown,
                    treeId: "T1", startUT: 100.0),
                Entry("Bill Kerman", "seg3", "Kerbal X", 300.0, KerbalEndState.Recovered,
                    treeId: "T1", startUT: 200.0),
            };
            var missions = new Dictionary<string, string>(StringComparer.Ordinal)
                { { "seg2", "First Steps" } };

            List<KerbalsPresentation.FlightGroup> groups = Flights(entries, missions);

            KerbalsPresentation.FlightGroup g = Assert.Single(groups);
            KerbalsPresentation.FlightRow row = Assert.Single(g.Rows);
            // Date is the kerbal's FIRST segment start in the tree, not any segment end.
            Assert.Equal("D10", row.DateText);
            Assert.Equal(300.0, row.EndUT);
            Assert.Equal("First Steps", row.MissionText);
            Assert.Equal("Recovered", row.OutcomeText);
            Assert.Equal(KerbalEndState.Recovered, row.EndState);
            Assert.Equal("T1", row.MissionKey);
            // The segments are counted and listed rather than drawn.
            Assert.Equal(3, row.SegmentCount);
            Assert.Equal("3 segments: Still aboard, Outcome unknown, Recovered",
                row.SegmentSummaryText);
            // The Timeline jump lands on where the mission GOT TO.
            Assert.Equal("seg3", row.RecordingId);
            // And the header counts missions, not segments.
            Assert.Equal("Bill Kerman - 1 mission: 1 recovered", g.HeaderText);
        }

        [Fact]
        public void Missions_TwoTreesStayTwoRowsOrderedByTheDateColumn()
        {
            var entries = new List<KerbalsWindowUI.CrewEndStateEntry>
            {
                Entry("Bill Kerman", "b1", "Kerbal X", 9000.0, KerbalEndState.Aboard,
                    treeId: "T2", startUT: 400.0),
                Entry("Bill Kerman", "b2", "Kerbal X", 9100.0, KerbalEndState.Aboard,
                    treeId: "T2", startUT: 9000.0),
                Entry("Bill Kerman", "a1", "Kerbal X", 380.0, KerbalEndState.Recovered,
                    treeId: "T1", startUT: 25.0),
            };

            List<KerbalsPresentation.FlightGroup> groups = Flights(entries);

            KerbalsPresentation.FlightGroup g = Assert.Single(groups);
            Assert.Equal(2, g.Rows.Count);
            Assert.Equal(new[] { "T1", "T2" }, g.Rows.Select(r => r.MissionKey).ToArray());
            Assert.Equal(new[] { "D25", "D400" }, g.Rows.Select(r => r.DateText).ToArray());
            Assert.Equal(new[] { 1, 2 }, g.Rows.Select(r => r.SegmentCount).ToArray());
            Assert.Equal("Bill Kerman - 2 missions: 1 recovered, 1 aboard", g.HeaderText);
            // A single-segment mission still names its one segment, so the hover text is
            // never blank.
            Assert.Equal("1 segment: Recovered", g.Rows[0].SegmentSummaryText);
        }

        // catches: a standalone recording (an EVA branch that is its own tree, a pre-tree
        // recording) being folded into a neighbour, or dropped, by the tree grouping.
        [Fact]
        public void Missions_ARecordingWithNoTreeIsItsOwnMissionKeyedByItsOwnId()
        {
            var entries = new List<KerbalsWindowUI.CrewEndStateEntry>
            {
                Entry("Bill Kerman", "eva1", "Bill Kerman EVA", 50.0,
                    KerbalEndState.Recovered),
                Entry("Bill Kerman", "eva2", "Bill Kerman EVA", 60.0,
                    KerbalEndState.Recovered),
            };

            List<KerbalsPresentation.FlightGroup> groups = Flights(entries);

            Assert.Equal(2, groups[0].Rows.Count);
            Assert.Equal(new[] { "eva1", "eva2" },
                groups[0].Rows.Select(r => r.MissionKey).ToArray());
            Assert.Equal("Bill Kerman - 2 missions: 2 recovered", groups[0].HeaderText);
        }

        [Fact]
        public void Missions_TheMissionKeyIsTheTreeWhenThereIsOneAndTheRecordingOtherwise()
        {
            Assert.Equal("T1", KerbalsPresentation.MissionKeyOf(
                Entry("Bill Kerman", "r1", "A", 1.0, KerbalEndState.Aboard, treeId: "T1")));
            Assert.Equal("r1", KerbalsPresentation.MissionKeyOf(
                Entry("Bill Kerman", "r1", "A", 1.0, KerbalEndState.Aboard)));
        }

        // catches: the one date the window shows for a mission drifting between tabs.
        // The Flights Date column and the Roster's Lost hover both read the mission's
        // LAUNCH (the Since column that read its end is gone).
        [Fact]
        public void Missions_TheLostHoverAndTheDateColumnDateAMissionTheSameWay()
        {
            var roster = new List<KerbalsPresentation.RosterKerbal>
                { Kerbal("Bill Kerman", "Engineer") };
            var res = new Dictionary<string, KerbalsModule.KerbalReservation>(
                StringComparer.Ordinal)
            {
                { "Bill Kerman", Res("Bill Kerman", double.PositiveInfinity, true) },
            };
            var flights = Flights(new List<KerbalsWindowUI.CrewEndStateEntry>
            {
                Entry("Bill Kerman", "seg1", "Kerbal X", 8949.0, KerbalEndState.Aboard,
                    treeId: "T1", startUT: 391.0),
                Entry("Bill Kerman", "seg2", "Kerbal X", 8951.0, KerbalEndState.Dead,
                    treeId: "T1", startUT: 8949.0),
            });

            KerbalsPresentation.RosterRowSet set = KerbalsPresentation.BuildRosterRows(
                roster, null, res, null, flights, null, FakeDate);

            KerbalsPresentation.RosterRow row = Find(set, "Bill Kerman");
            Assert.Equal("D391", flights[0].Rows[0].DateText);
            Assert.Contains("(launched D391)", row.StatusTooltipText);
            Assert.Equal("Kerbal X - Lost", row.LastFlightText);
        }

        // catches: "latest flight" being read off the last ROW rather than the
        // latest-ENDING mission, which the Date-column ordering does not guarantee once
        // two missions overlap.
        [Fact]
        public void Missions_TheLatestFlightIsTheLatestEndingMissionNotTheLastRow()
        {
            var roster = new List<KerbalsPresentation.RosterKerbal>
                { Kerbal("Bill Kerman", "Engineer") };
            var res = new Dictionary<string, KerbalsModule.KerbalReservation>(
                StringComparer.Ordinal)
            {
                { "Bill Kerman", Res("Bill Kerman", 9999.0) },
            };
            // The SECOND mission by start date ends FIRST - a short hop launched during a
            // long station stay.
            var flights = Flights(new List<KerbalsWindowUI.CrewEndStateEntry>
            {
                Entry("Bill Kerman", "stay", "Station", 5000.0, KerbalEndState.Aboard,
                    treeId: "T1", startUT: 100.0),
                Entry("Bill Kerman", "hop", "Hopper", 900.0, KerbalEndState.Recovered,
                    treeId: "T2", startUT: 200.0),
            });

            KerbalsPresentation.RosterRowSet set = KerbalsPresentation.BuildRosterRows(
                roster, null, res, null, flights, null, FakeDate);

            // Row order is by the Date column; the roster picks by end UT regardless.
            Assert.Equal(new[] { "T1", "T2" },
                flights[0].Rows.Select(r => r.MissionKey).ToArray());
            Assert.Equal("Station - Still aboard", Find(set, "Bill Kerman").LastFlightText);
            Assert.Equal("stay", Find(set, "Bill Kerman").LastFlightRecordingId);
        }

        // catches: the crew note answering "who flew the LAST segment" rather than "did a
        // stand-in fly this mission" - a stand-in who flew the launch and handed over
        // mid-mission still flew it.
        [Fact]
        public void Missions_TheCrewNoteNamesTheFirstStandInAcrossTheMissionsSegments()
        {
            var entries = new List<KerbalsWindowUI.CrewEndStateEntry>
            {
                Entry("Jebediah Kerman", "seg1", "Kerbal X", 100.0, KerbalEndState.Aboard,
                    treeId: "T1", startUT: 10.0),
                Entry("Jebediah Kerman", "seg2", "Kerbal X", 200.0, KerbalEndState.Aboard,
                    treeId: "T1", startUT: 100.0),
            };
            // Lars flew the first segment; Jeb himself the second.
            var rawCrew = new Dictionary<string, IReadOnlyCollection<string>>(
                StringComparer.Ordinal)
            {
                { "seg1", new List<string> { "Lars Kerman" } },
                { "seg2", new List<string> { "Jebediah Kerman" } },
            };

            List<KerbalsPresentation.FlightGroup> groups = Flights(
                entries, rawCrew: rawCrew, slots: JebCoveredByLars());

            Assert.Equal("Lars Kerman", groups[0].Rows[0].StandInName);
            Assert.Equal("Kerbal X (flown by Lars Kerman)", groups[0].Rows[0].MissionCellText);
        }

        [Fact]
        public void FormatMissionCell_AppendsTheStandInOnlyWhenOneFlew()
        {
            Assert.Equal("Mun Hopper", KerbalsPresentation.FormatMissionCell("Mun Hopper", null));
            Assert.Equal("Mun Hopper", KerbalsPresentation.FormatMissionCell("Mun Hopper", ""));
            Assert.Equal("Mun Hopper (flown by Lars Kerman)",
                KerbalsPresentation.FormatMissionCell("Mun Hopper", "Lars Kerman"));
        }

        [Fact]
        public void Missions_TheOutcomeHoverCarriesTheSegmentListOnlyWhenThereIsMoreThanOne()
        {
            var single = new KerbalsPresentation.FlightRow
            {
                EndState = KerbalEndState.Recovered,
                SegmentCount = 1,
                SegmentSummaryText = "1 segment: Recovered"
            };
            Assert.Equal("The flight ended with this kerbal recovered.",
                KerbalsWindowUI.DescribeFlightRowOutcome(single));

            var many = new KerbalsPresentation.FlightRow
            {
                EndState = KerbalEndState.Recovered,
                SegmentCount = 3,
                SegmentSummaryText = "3 segments: Still aboard, Still aboard, Recovered"
            };
            Assert.Equal(
                "The flight ended with this kerbal recovered. "
                + "3 segments: Still aboard, Still aboard, Recovered.",
                KerbalsWindowUI.DescribeFlightRowOutcome(many));
        }

        // ------------------------------------------------------------------
        // R2 - "Stand-in for X" is a per-MEMBER fact (review probes A-F)
        // ------------------------------------------------------------------

        /// <summary>Jeb's slot with a three-name chain, so the active occupant and the
        /// displaced members are distinguishable.</summary>
        private static Dictionary<string, KerbalsModule.KerbalSlot> JebCoveredByThree()
        {
            return new Dictionary<string, KerbalsModule.KerbalSlot>(StringComparer.Ordinal)
            {
                { "Jebediah Kerman",
                    Slot("Jebediah Kerman", "Pilot",
                        new List<string> { "Lars Kerman", "Mies Kerman" }) },
            };
        }

        // PROBE D: the owner is back, so the chain's first member is DISPLACED - and a
        // displaced member is not standing in for anybody. Before this, the row read
        // "Stand-in for Jebediah Kerman" while the owner's own expansion, one line up,
        // called the same name "displaced".
        [Fact]
        public void Probe_ADisplacedChainMemberIsNotAStandIn()
        {
            var roster = new List<KerbalsPresentation.RosterKerbal>
            {
                Kerbal("Jebediah Kerman", "Pilot"),
                Kerbal("Lars Kerman", "Pilot"),
            };
            // No reservation on Jeb: he is the active occupant of his own slot.
            KerbalsPresentation.RosterRowSet set = Roster(roster, JebCoveredByLars());

            KerbalsPresentation.RosterRow lars = Find(set, "Lars Kerman");
            Assert.Equal(KerbalsPresentation.RosterStatus.Available, lars.Status);
            Assert.Equal("Available", lars.StatusText);
            // Listed under the owner, carrying its chain place.
            Assert.Equal(KerbalsWindowUI.ChainMemberStatus.Displaced, lars.MemberStatus);
            Assert.Equal(1, lars.Depth);
        }

        // PROBE C: the owner is permanently gone, so GetActiveChainIndex answers
        // NoActiveChainOccupant and every member is freed - none of them is still covering
        // a slot nobody will return to.
        [Fact]
        public void Probe_ADeadOwnerFreesHisChainMembersRatherThanKeepingThemStandingIn()
        {
            var roster = new List<KerbalsPresentation.RosterKerbal>
            {
                Kerbal("Jebediah Kerman", "Pilot"),
                Kerbal("Lars Kerman", "Pilot"),
                Kerbal("Mies Kerman", "Engineer"),
            };
            var slots = new Dictionary<string, KerbalsModule.KerbalSlot>(StringComparer.Ordinal)
            {
                { "Jebediah Kerman",
                    Slot("Jebediah Kerman", "Pilot",
                        new List<string> { "Lars Kerman", "Mies Kerman" },
                        permanentlyGone: true) },
            };
            var res = new Dictionary<string, KerbalsModule.KerbalReservation>(
                StringComparer.Ordinal);

            KerbalsPresentation.RosterRowSet set = Roster(roster, slots, res);

            Assert.Equal("Lost", Find(set, "Jebediah Kerman").StatusText);
            Assert.Equal("Available", Find(set, "Lars Kerman").StatusText);
            Assert.Equal("Available", Find(set, "Mies Kerman").StatusText);
        }

        // PROBE B: an ACTIVE stand-in who is also aboard a craft. The vessel goes inline
        // when the composed cell fits its column and into the hover text when it does not,
        // because a clipped cell reads as a shorter status rather than as an overflow.
        [Fact]
        public void Probe_AnActiveStandInAboardACraftNamesTheVesselInlineWhenItFits()
        {
            var slots = new Dictionary<string, KerbalsModule.KerbalSlot>(StringComparer.Ordinal)
            {
                { "Jeb", Slot("Jeb", "Pilot", new List<string> { "Lars Kerman" }) },
            };
            var res = new Dictionary<string, KerbalsModule.KerbalReservation>(
                StringComparer.Ordinal)
            {
                { "Jeb", Res("Jeb", 500.0) },
            };
            var roster = new List<KerbalsPresentation.RosterKerbal>
                { Kerbal("Lars Kerman", "Pilot", "Pod") };

            KerbalsPresentation.RosterRowSet set = Roster(roster, slots, res);

            KerbalsPresentation.RosterRow lars = Find(set, "Lars Kerman");
            Assert.Equal(KerbalsPresentation.RosterStatus.StandIn, lars.Status);
            // "Stand-in for Jeb (aboard Pod)" is 29 chars against the column's 31.
            Assert.Equal("Stand-in for Jeb (aboard Pod)", lars.StatusText);
            Assert.Null(lars.StatusTooltipText);
        }

        [Fact]
        public void Probe_AnActiveStandInAboardALongNamedCraftMovesTheVesselToTheHoverText()
        {
            var res = new Dictionary<string, KerbalsModule.KerbalReservation>(
                StringComparer.Ordinal)
            {
                { "Jebediah Kerman", Res("Jebediah Kerman", 500.0) },
            };
            var roster = new List<KerbalsPresentation.RosterKerbal>
                { Kerbal("Lars Kerman", "Pilot", "mk1-capsule") };

            KerbalsPresentation.RosterRowSet set = Roster(
                roster, JebCoveredByLars(), res);

            KerbalsPresentation.RosterRow lars = Find(set, "Lars Kerman");
            Assert.Equal(KerbalsPresentation.RosterStatus.StandIn, lars.Status);
            Assert.Equal("Stand-in for Jebediah Kerman", lars.StatusText);
            Assert.Equal("Standing in for Jebediah Kerman; aboard mk1-capsule.",
                lars.StatusTooltipText);
            // The budget is derived from the column width, not hand-typed.
            Assert.Equal(31, KerbalsPresentation.StatusCellMaxChars);
        }

        // Which statuses carry hover text: Lost (the way back) and Reserved (the release
        // rule) always; a stand-in only when his vessel does not fit inline; the rest never.
        [Fact]
        public void Probe_TheStatusHoverTextIsLostReservedOrAnOverflowingStandInOnly()
        {
            foreach (KerbalsPresentation.RosterStatus s in
                     Enum.GetValues(typeof(KerbalsPresentation.RosterStatus)))
            {
                string tip = KerbalsPresentation.FormatStatusTooltip(
                    s, "Jebediah Kerman", "Jebediah Kerman", null, null, "mk1-capsule", false);
                switch (s)
                {
                    case KerbalsPresentation.RosterStatus.Lost:
                        Assert.Equal("Lost on a committed flight. "
                                     + KerbalsPresentation.LostReFlyRemedy, tip);
                        break;
                    case KerbalsPresentation.RosterStatus.Reserved:
                        Assert.EndsWith(KerbalsPresentation.ReservationHoldRule, tip);
                        break;
                    case KerbalsPresentation.RosterStatus.StandIn:
                        Assert.NotNull(tip);
                        break;
                    default:
                        Assert.Null(tip);
                        break;
                }
            }
            // A stand-in who is aboard nothing says everything inline.
            Assert.Null(KerbalsPresentation.FormatStatusTooltip(
                KerbalsPresentation.RosterStatus.StandIn, "Lars Kerman", "Jebediah Kerman",
                null, null, null, false));
        }

        // catches: an EVA kerbal reading "Assigned (Jebediah Kerman)" - an EVA kerbal is
        // his own vessel, so the assigned form named himself (GUI-6 postcrew capture).
        [Fact]
        public void Roster_AKerbalOnEvaReadsOnEva()
        {
            var roster = new List<KerbalsPresentation.RosterKerbal>
            {
                new KerbalsPresentation.RosterKerbal
                {
                    Name = "Jebediah Kerman",
                    Trait = "Pilot",
                    AssignedVesselName = "Jebediah Kerman",
                    AssignedVesselIsEva = true
                },
            };

            KerbalsPresentation.RosterRow jeb = Find(Roster(roster), "Jebediah Kerman");

            Assert.Equal(KerbalsPresentation.RosterStatus.Assigned, jeb.Status);
            Assert.Equal("On EVA", jeb.StatusText);
        }

        [Fact]
        public void Roster_AStandInOnEvaSaysSoInlineWhenItFits()
        {
            Assert.Equal("Stand-in for Jeb (on EVA)",
                KerbalsPresentation.FormatStatus(KerbalsPresentation.RosterStatus.StandIn,
                    "Lars Kerman", "Jeb", null, "Lars Kerman", true));
            Assert.Equal("Standing in for Jebediah Kerman; on EVA.",
                KerbalsPresentation.FormatStatusTooltip(KerbalsPresentation.RosterStatus.StandIn,
                    "Lars Kerman", "Jebediah Kerman", null, null, "Lars Kerman", true));
        }

        // PROBE E: a retired kerbal who is still sitting in a craft reads Retired, not
        // Assigned - Retired outranks both the assignment and the chain.
        [Fact]
        public void Probe_ARetiredKerbalAboardACraftStillReadsRetired()
        {
            var roster = new List<KerbalsPresentation.RosterKerbal>
                { Kerbal("Hanley Kerman", "Pilot", "Station") };

            KerbalsPresentation.RosterRowSet set = Roster(
                roster, retired: new List<string> { "Hanley Kerman" });

            Assert.Equal("Retired", Find(set, "Hanley Kerman").StatusText);
        }

        // PROBE A / R10: Retired outranks Reserved. UNREACHABLE in production -
        // KerbalsModule.ComputeRetiredSet only marks names that are NOT reserved, so the
        // two are mutually exclusive by construction - which is exactly why the ordering
        // needs a cell: nothing else would notice it being swapped.
        [Fact]
        public void Probe_RetiredOutranksReservedEvenThoughTheTwoCannotCoOccur()
        {
            Assert.Equal(
                KerbalsPresentation.RosterStatus.Retired,
                KerbalsPresentation.ClassifyStatus(
                    "Hanley Kerman",
                    ownerPermanentlyGone: false,
                    retired: true,
                    reservation: new KerbalsModule.KerbalReservation
                    {
                        KerbalName = "Hanley Kerman",
                        ReservedUntilUT = 500.0,
                        IsPermanent = false
                    },
                    memberStatus: KerbalsWindowUI.ChainMemberStatus.Active,
                    assignedVesselName: "Station"));
            // And a PERMANENT reservation still outranks Retired, because that is a loss.
            Assert.Equal(
                KerbalsPresentation.RosterStatus.Lost,
                KerbalsPresentation.ClassifyStatus(
                    "Hanley Kerman",
                    ownerPermanentlyGone: false,
                    retired: true,
                    reservation: new KerbalsModule.KerbalReservation
                    {
                        KerbalName = "Hanley Kerman",
                        ReservedUntilUT = double.PositiveInfinity,
                        IsPermanent = true
                    },
                    memberStatus: null,
                    assignedVesselName: null));
        }

        // PROBE F: a retired chain member is not a stand-in either, and his row is not
        // expandable - the two halves of the same finding.
        [Fact]
        public void Probe_ARetiredChainMemberIsNeitherAStandInNorExpandable()
        {
            var roster = new List<KerbalsPresentation.RosterKerbal>
            {
                Kerbal("Jebediah Kerman", "Pilot"),
                Kerbal("Lars Kerman", "Pilot"),
            };

            KerbalsPresentation.RosterRowSet set = Roster(
                roster, JebCoveredByLars(), JebReserved(),
                retired: new List<string> { "Lars Kerman" });

            KerbalsPresentation.RosterRow lars = Find(set, "Lars Kerman");
            Assert.Equal("Retired", lars.StatusText);
            Assert.Equal(0, lars.SlotMemberCount);
        }

        [Fact]
        public void Probe_OnlyTheActiveChainStatusReadsAsAStandIn()
        {
            foreach (KerbalsWindowUI.ChainMemberStatus ms in
                     Enum.GetValues(typeof(KerbalsWindowUI.ChainMemberStatus)))
            {
                KerbalsPresentation.RosterStatus status =
                    KerbalsPresentation.ClassifyStatus(
                        "Lars Kerman",
                        ownerPermanentlyGone: false,
                        retired: false,
                        reservation: null,
                        memberStatus: ms,
                        assignedVesselName: null);
                if (ms == KerbalsWindowUI.ChainMemberStatus.Active)
                    Assert.Equal(KerbalsPresentation.RosterStatus.StandIn, status);
                else
                    Assert.Equal(KerbalsPresentation.RosterStatus.Available, status);
            }
            // A row with no slot at all passes null, and that is not a stand-in either.
            Assert.Equal(
                KerbalsPresentation.RosterStatus.Available,
                KerbalsPresentation.ClassifyStatus(
                    "Bob Kerman", false, false, null, null, null));
        }

        // ------------------------------------------------------------------
        // R1 - the live-crew refresh, and R11 - the expanded-count clamp
        // ------------------------------------------------------------------

        // catches: the view-model cache being invalidated only from the LEDGER hook while
        // both tabs also read live state (the stock roster walk and the crew-to-vessel
        // map), so a transfer / EVA / board / hire left a stale Assigned cell until some
        // unrelated ledger write happened to drop it.
        [Fact]
        public void LiveCrewRefresh_LogsOneLineNamingTheEventAndWhetherItDroppedACache()
        {
            Assert.Equal(
                "KerbalsWindow: live crew state changed (onCrewTransferred) - "
                + "cache invalidated",
                KerbalsWindowUI.DescribeLiveCrewRefresh("onCrewTransferred", true));
            Assert.Equal(
                "KerbalsWindow: live crew state changed (onCrewOnEva) - "
                + "cache already empty",
                KerbalsWindowUI.DescribeLiveCrewRefresh("onCrewOnEva", false));
        }

        // The eight events the window subscribes, asserted against the subscribe method's
        // OWN source rather than against a second hand-written list: a new event added to
        // SubscribeLiveCrewEvents without a row here (or vice versa) reds this cell.
        [Fact]
        public void LiveCrewRefresh_TheDocumentedEventSetIsExactlyWhatTheWindowSubscribes()
        {
            string src = TooltipEchoBudgetTests.ReadParsekSource("UI/KerbalsWindowUI.cs");
            int start = src.IndexOf("internal void SubscribeLiveCrewEvents()",
                StringComparison.Ordinal);
            Assert.True(start >= 0, "SubscribeLiveCrewEvents moved or was renamed.");
            int end = src.IndexOf("internal void UnsubscribeLiveCrewEvents()",
                start, StringComparison.Ordinal);
            Assert.True(end > start, "UnsubscribeLiveCrewEvents moved or was renamed.");
            string body = src.Substring(start, end - start);

            var subscribed = new List<string>();
            foreach (string name in KerbalsWindowUI.LiveCrewRefreshEvents)
            {
                if (body.Contains("GameEvents." + name + ".Add(")) subscribed.Add(name);
            }
            Assert.Equal(
                KerbalsWindowUI.LiveCrewRefreshEvents.Length, subscribed.Count);
            // And the unsubscribe half drops every one of them, so the window's
            // subscriptions die with its scene's ParsekUI.
            string tail = src.Substring(end);
            foreach (string name in KerbalsWindowUI.LiveCrewRefreshEvents)
            {
                Assert.Contains("GameEvents." + name + ".Remove(", tail);
            }
            // Eight Add( calls in the body, so no event is listed and then not wired.
            Assert.Equal(
                KerbalsWindowUI.LiveCrewRefreshEvents.Length,
                CountOccurrences(body, ".Add("));
        }

        private static int CountOccurrences(string haystack, string needle)
        {
            int n = 0, i = 0;
            while (true)
            {
                int at = haystack.IndexOf(needle, i, StringComparison.Ordinal);
                if (at < 0) return n;
                n++;
                i = at + needle.Length;
            }
        }

        [Fact]
        public void LiveCrewRefresh_ParsekUiSubscribesAndUnsubscribesWhereTheLedgerHookIs()
        {
            string src = TooltipEchoBudgetTests.ReadParsekSource("ParsekUI.cs");
            // Both constructors subscribe (one per scene host), and Cleanup drops them.
            Assert.Equal(2, CountOccurrences(src, "kerbalsUI.SubscribeLiveCrewEvents();"));
            Assert.Equal(1, CountOccurrences(src, "kerbalsUI.UnsubscribeLiveCrewEvents();"));
            Assert.Equal(2, CountOccurrences(
                src, "LedgerOrchestrator.OnTimelineDataChanged += OnTimelineDataChanged;"));
        }

        // R11 - catches: a negative expanded= term on the wire. foldedKerbals can hold a
        // name the CURRENT view model no longer carries (the save reloaded, a recording was
        // deleted), and the count is every group minus the folded ones.
        [Fact]
        public void ExpandSeam_TheFlightCountClampsAtZeroForAStaleFoldedName()
        {
            var ui = new KerbalsWindowUI(null);
            ui.CachedViewModelForTesting = SeededVM();

            // One group in the model, and three folded names - two of which it never had.
            ui.foldedKerbals.Add("Jebediah Kerman");
            ui.foldedKerbals.Add("Gone Kerman");
            ui.foldedKerbals.Add("AlsoGone Kerman");

            Assert.Equal(0, ui.ExpandedFlightCountForTesting);

            // With no view model at all there are no keys, so the count is zero rather
            // than minus three.
            ui.CachedViewModelForTesting = null;
            Assert.Equal(0, ui.ExpandedFlightCountForTesting);
        }

        // catches: the two sizing numbers drifting apart - the minimum must stay below the
        // first-open width, and the minimum must cover the fixed columns it was derived
        // from.
        [Fact]
        public void Sizing_TheMinimumWidthCoversTheFixedRosterColumnsAndTheirChrome()
        {
            // 430 fixed + 8 inter-column margin + 4 + 100 sliver + 28 chrome + 16 gutter.
            float fixedColumns = KerbalsWindowUI.ColW_RosterName + KerbalsWindowUI.ColW_RosterStatus;
            Assert.Equal(430f, fixedColumns);
            Assert.Equal(KerbalsWindowUI.MinWindowWidth,
                fixedColumns + 8f + 4f + 100f + 28f
                + ParsekUI.DefaultVerticalScrollbarFootprintWidth);
            Assert.Equal(586f, KerbalsWindowUI.MinWindowWidth);
            Assert.True(KerbalsWindowUI.MinWindowWidth
                        > fixedColumns + 8f + 4f + 28f
                          + ParsekUI.DefaultVerticalScrollbarFootprintWidth,
                "the minimum width no longer leaves the expanding column a sliver");
            Assert.True(KerbalsWindowUI.MinWindowWidth < KerbalsWindowUI.DefaultWindowWidth,
                "the minimum width must stay below the first-open width");
        }
    }
}
