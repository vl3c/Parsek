using System;
using System.Collections.Generic;

namespace Parsek.UI.Gallery
{
    /// <summary>
    /// Catalogue states for the Kerbals window (both tabs).
    ///
    /// <para><b>Every row here comes out of the real pure builders.</b> A state assembles
    /// the INPUTS the window gathers - the stock roster rows, the ledger's slots,
    /// reservations and retired set, the per-recording crew end states, the raw crew and
    /// the replacement map - and then calls
    /// <see cref="KerbalsPresentation.BuildFlightRows"/> and
    /// <see cref="KerbalsPresentation.BuildRosterRows"/>, which is exactly what
    /// <c>KerbalsWindowUI.BuildViewModel</c> does with live inputs. Not one status word,
    /// date cell or fold header is typed here.</para>
    ///
    /// <para><b>Why not <c>BuildViewModel</c> itself.</b> That entry point takes
    /// <c>IReadOnlyList&lt;Recording&gt;</c> and derives the crew end states from them, so
    /// using it would mean fabricating <c>Recording</c> objects - the store-shaped mocking
    /// philosophy 2 forbids. The two builders below are the same code one layer in, taking
    /// the end states directly.</para>
    ///
    /// <para><b>The date formatter is the window's own.</b>
    /// <c>KerbalsWindowUI.FormatRowDate</c> wraps <c>KSPUtil.PrintDateCompact</c> with an
    /// invariant F0 fallback, so a date cell in a mocked capture is the calendar string
    /// the game would print, and the headless unit suite still gets a deterministic
    /// number.</para>
    /// </summary>
    internal static class GuiMockKerbalsStates
    {
        private const string RosterTab = "roster";
        private const string FlightsTab = "outcomes";

        // Window sizing: the Roster tab's own minimum is 700 (KerbalsWindowUI.MinWindowWidth)
        // and its four columns want ~760 to show "Last flight" without clipping. 900x520
        // leaves every catalogue state's rows on screen without a scroll.
        private const int RectW = 900;
        private const int RectH = 520;

        internal static void Append(List<GuiMockState> into)
        {
            // ---------- Roster tab ----------

            into.Add(Roster(
                "kerbals.roster.lost",
                "The window's most consequential state: a kerbal lost on a recorded flight, "
                + "red deadStyle, dated by the flight that killed him.",
                new[] { "RosterStatus.Lost", "KerbalEndState.Dead" },
                BuildLostRoster));

            into.Add(Roster(
                "kerbals.roster.retired",
                "A retired stand-in - a whole status word with no picture in the current design.",
                new[] { "RosterStatus.Retired", "ChainMemberStatus.Retired" },
                BuildRetiredRoster,
                expand: new[] { "Jebediah Kerman" }));

            into.Add(Roster(
                "kerbals.roster.standin-active",
                "An active stand-in covering a reserved owner's slot.",
                new[] { "RosterStatus.StandIn", "ChainMemberStatus.Active" },
                () => BuildStandInRoster(aboardVessel: null)));

            // NOTE, and it is a product finding rather than a catalogue choice: there is
            // no INLINE "Stand-in for X (aboard Y)" state here because the product cannot
            // produce one for any real kerbal. FormatStatus keeps the vessel inline only
            // while the composed text fits StatusCellMaxChars (220px / 7px = 31), and
            // "Stand-in for " (13) + " (aboard " (9) + ")" (1) leaves 8 characters for
            // the owner's name AND the vessel's together - while the shortest stock
            // kerbal name is "Bob Kerman" (10). Pinned by
            // GuiMockCatalogueTests.TheInlineStandInVesselFormIsUnreachableForRealNames
            // and filed in docs/dev/todo-and-known-bugs.md.
            into.Add(Roster(
                "kerbals.roster.standin-aboard",
                "A stand-in who is also aboard a craft: the vessel moves into the cell's "
                + "hover text, because the inline form does not fit the 220px column for "
                + "any real kerbal name.",
                new[] { "RosterStatus.StandIn" },
                () => BuildStandInRoster(
                    aboardVessel: "Duna Surface Sample Return Ascent Stage")));

            into.Add(Roster(
                "kerbals.roster.reserved-dated",
                "The dated half of the reservation form - every capture reads "
                + "'until recovery'.",
                new[] { "RosterStatus.Reserved" },
                () => BuildReservedRoster(permanent: false, untilUT: 2_160_000.0)));

            into.Add(Roster(
                "kerbals.roster.reserved-for-owner",
                "A reserved STAND-IN, which names the owner whose slot he serves - the case "
                + "the branch was written for.",
                new[] { "RosterStatus.Reserved" },
                BuildReservedStandInRoster));

            into.Add(Roster(
                "kerbals.roster.chain-two-deep",
                "A two-deep replacement chain expanded: the mid-branch glyph and a "
                + "(displaced) member, neither of which any capture shows.",
                new[] { "ChainMemberStatus.Displaced", "ChainMemberStatus.Active" },
                BuildTwoDeepChainRoster,
                expand: new[] { "Jebediah Kerman" }));

            into.Add(Roster(
                "kerbals.roster.owner-permanently-gone",
                "A slot whose owner is permanently gone: no chain member is Active, so the "
                + "freed stand-ins stop being labelled as covering a slot nobody returns to.",
                new[] { "RosterStatus.Lost", "ChainMemberStatus.Displaced" },
                BuildOwnerGoneRoster,
                expand: new[] { "Jebediah Kerman" }));

            into.Add(Roster(
                "kerbals.roster.all-statuses",
                "The overview: all six roster statuses in one table, which is the picture a "
                + "reviewer judging the status column needs.",
                new[] { "RosterStatus.Lost", "RosterStatus.Retired", "RosterStatus.Reserved",
                        "RosterStatus.StandIn", "RosterStatus.Assigned",
                        "RosterStatus.Available" },
                BuildAllStatusRoster));

            // ---------- Flights tab ----------

            into.Add(Flights(
                "kerbals.flights.lost-outcome",
                "The red Lost outcome word and the 'n lost' bucket in the group header.",
                new[] { "KerbalEndState.Dead" },
                () => BuildFlights(FlightShape.Lost)));

            into.Add(Flights(
                "kerbals.flights.standin-crew",
                "The Crew column's entire reason to exist: 'as <stand-in>', unphotographed "
                + "even on the fixture that has three stand-ins.",
                new[] { "KerbalEndState.Recovered" },
                () => BuildFlights(FlightShape.StandInCrew)));

            into.Add(Flights(
                "kerbals.flights.multi-segment",
                "One mission that collapsed three recorded segments - the row the "
                + "2026-09-15 mission collapse produced.",
                new[] { "KerbalEndState.Recovered", "KerbalEndState.Aboard" },
                () => BuildFlights(FlightShape.MultiSegment)));

            into.Add(Flights(
                "kerbals.flights.mixed-buckets",
                "A group header carrying all four buckets at once.",
                new[] { "KerbalEndState.Recovered", "KerbalEndState.Dead",
                        "KerbalEndState.Aboard", "KerbalEndState.Unknown" },
                () => BuildFlights(FlightShape.MixedBuckets)));

            into.Add(Flights(
                "kerbals.flights.unknown-outcome",
                "A mission with no recorded ending: 'Outcome unknown'.",
                new[] { "KerbalEndState.Unknown" },
                () => BuildFlights(FlightShape.Unknown)));
        }

        // ----- state factories -----

        private static GuiMockState Roster(string id, string note, string[] covers,
                                           Func<KerbalsWindowUI.KerbalsViewModel> build,
                                           string[] expand = null)
            => New(id, RosterTab, note, covers, build, expand);

        private static GuiMockState Flights(string id, string note, string[] covers,
                                            Func<KerbalsWindowUI.KerbalsViewModel> build)
            => New(id, FlightsTab, note, covers, build, null);

        private static GuiMockState New(string id, string tab, string note, string[] covers,
                                        Func<KerbalsWindowUI.KerbalsViewModel> build,
                                        string[] expand)
        {
            return new GuiMockState
            {
                Id = id,
                Window = GuiMockSession.KerbalsWindow,
                Tab = tab,
                ExpandKeys = expand ?? new string[0],
                RectW = RectW,
                RectH = RectH,
                Covers = covers,
                Note = note,
                Build = () => new GuiMockPayload
                {
                    Window = GuiMockSession.KerbalsWindow,
                    Kerbals = build(),
                },
            };
        }

        // ----- the input assemblers -----
        //
        // One small builder type so each state reads as a list of facts about a save
        // rather than as ten dictionary literals. It holds INPUTS only; every string the
        // window draws is produced by the two pure builders at the bottom.

        private sealed class RosterInputs
        {
            internal readonly List<KerbalsPresentation.RosterKerbal> Roster =
                new List<KerbalsPresentation.RosterKerbal>();
            internal readonly Dictionary<string, KerbalsModule.KerbalSlot> Slots =
                new Dictionary<string, KerbalsModule.KerbalSlot>(StringComparer.Ordinal);
            internal readonly Dictionary<string, KerbalsModule.KerbalReservation> Reservations =
                new Dictionary<string, KerbalsModule.KerbalReservation>(StringComparer.Ordinal);
            internal readonly List<string> Retired = new List<string>();
            internal readonly List<KerbalsWindowUI.CrewEndStateEntry> EndStates =
                new List<KerbalsWindowUI.CrewEndStateEntry>();
            internal readonly Dictionary<string, string> MissionNames =
                new Dictionary<string, string>(StringComparer.Ordinal);
            internal readonly Dictionary<string, IReadOnlyCollection<string>> RawCrew =
                new Dictionary<string, IReadOnlyCollection<string>>(StringComparer.Ordinal);
            internal readonly Dictionary<string, string> Replacements =
                new Dictionary<string, string>(StringComparer.Ordinal);

            internal RosterInputs Kerbal(string name, string trait, string aboard = null)
            {
                Roster.Add(new KerbalsPresentation.RosterKerbal
                {
                    Name = name,
                    Trait = trait,
                    AssignedVesselName = aboard,
                });
                return this;
            }

            internal RosterInputs Slot(string owner, string trait, bool ownerGone,
                                       params string[] chain)
            {
                var slot = new KerbalsModule.KerbalSlot
                {
                    OwnerName = owner,
                    OwnerTrait = trait,
                    OwnerPermanentlyGone = ownerGone,
                };
                slot.Chain.AddRange(chain);
                Slots[owner] = slot;
                return this;
            }

            internal RosterInputs Reserve(string name, double untilUT, bool permanent)
            {
                Reservations[name] = new KerbalsModule.KerbalReservation
                {
                    KerbalName = name,
                    ReservedUntilUT = untilUT,
                    IsPermanent = permanent,
                };
                return this;
            }

            internal RosterInputs Retire(string name)
            {
                Retired.Add(name);
                return this;
            }

            /// <summary>One recorded segment of one mission for one kerbal, plus the
            /// mission name and the crew actually recorded aboard.</summary>
            internal RosterInputs Segment(string kerbal, string recordingId, string treeId,
                                          string missionName, double startUT, double endUT,
                                          KerbalEndState endState,
                                          params string[] rawCrew)
            {
                EndStates.Add(new KerbalsWindowUI.CrewEndStateEntry
                {
                    KerbalName = kerbal,
                    RecordingName = missionName,
                    RecordingId = recordingId,
                    TreeId = treeId,
                    StartUT = startUT,
                    EndUT = endUT,
                    EndState = endState,
                });
                MissionNames[recordingId] = missionName;
                if (rawCrew != null && rawCrew.Length > 0)
                    RawCrew[recordingId] = new List<string>(rawCrew);
                return this;
            }

            /// <summary>Runs the two REAL pure builders, exactly as
            /// <c>KerbalsWindowUI.BuildViewModel</c> does.</summary>
            internal KerbalsWindowUI.KerbalsViewModel Build()
            {
                var traits = new Dictionary<string, string>(StringComparer.Ordinal);
                for (int i = 0; i < Roster.Count; i++)
                    traits[Roster[i].Name] = Roster[i].Trait ?? "";
                foreach (var pair in Slots)
                    if (!traits.ContainsKey(pair.Key))
                        traits[pair.Key] = pair.Value.OwnerTrait ?? "";

                List<KerbalsPresentation.FlightGroup> flights =
                    KerbalsPresentation.BuildFlightRows(
                        EndStates, MissionNames, RawCrew, Replacements, Slots, traits,
                        KerbalsWindowUI.FormatRowDate);

                // The active-chain rule is the production one, reached through the pure
                // overload KerbalsModule exposes for exactly this - not a copy of it.
                KerbalsWindowUI.ActiveChainIndexFunc activeIndexOf =
                    slot => KerbalsModule.ResolveActiveChainIndex(
                        slot != null ? slot.OwnerName : null, slot, Reservations.ContainsKey);

                KerbalsPresentation.RosterRowSet rows = KerbalsPresentation.BuildRosterRows(
                    Roster, Slots, Reservations, Retired, flights, activeIndexOf,
                    KerbalsWindowUI.FormatRowDate);

                return new KerbalsWindowUI.KerbalsViewModel
                {
                    EndStates = EndStates,
                    Flights = flights,
                    Roster = rows,
                };
            }
        }

        // ----- the roster states -----

        private static KerbalsWindowUI.KerbalsViewModel BuildLostRoster()
        {
            var inputs = new RosterInputs()
                .Kerbal("Bill Kerman", "Engineer")
                .Kerbal("Valentina Kerman", "Pilot")
                .Slot("Jebediah Kerman", "Pilot", ownerGone: true)
                .Reserve("Jebediah Kerman", double.PositiveInfinity, permanent: true)
                .Segment("Jebediah Kerman", "rec-mun-1", "tree-mun", "Mun Landing 1",
                         180_000.0, 214_400.0, KerbalEndState.Dead);
            return inputs.Build();
        }

        private static KerbalsWindowUI.KerbalsViewModel BuildRetiredRoster()
        {
            var inputs = new RosterInputs()
                .Kerbal("Jebediah Kerman", "Pilot")
                .Kerbal("Lars Kerman", "Pilot")
                .Slot("Jebediah Kerman", "Pilot", ownerGone: false, "Lars Kerman")
                .Retire("Lars Kerman")
                .Segment("Jebediah Kerman", "rec-lko-1", "tree-lko", "LKO Shakedown",
                         90_000.0, 96_000.0, KerbalEndState.Recovered, "Lars Kerman");
            return inputs.Build();
        }

        private static KerbalsWindowUI.KerbalsViewModel BuildStandInRoster(string aboardVessel)
        {
            var inputs = new RosterInputs()
                .Kerbal("Jebediah Kerman", "Pilot")
                .Kerbal("Lars Kerman", "Pilot", aboard: aboardVessel)
                .Slot("Jebediah Kerman", "Pilot", ownerGone: false, "Lars Kerman")
                .Reserve("Jebediah Kerman", double.PositiveInfinity, permanent: false)
                .Segment("Jebediah Kerman", "rec-mun-2", "tree-mun2", "Mun Flyby",
                         120_000.0, 151_000.0, KerbalEndState.Aboard, "Lars Kerman");
            return inputs.Build();
        }

        private static KerbalsWindowUI.KerbalsViewModel BuildReservedRoster(
            bool permanent, double untilUT)
        {
            var inputs = new RosterInputs()
                .Kerbal("Bill Kerman", "Engineer")
                .Kerbal("Valentina Kerman", "Pilot")
                .Reserve("Valentina Kerman", untilUT, permanent)
                .Segment("Valentina Kerman", "rec-minmus-1", "tree-minmus", "Minmus Station",
                         1_900_000.0, 2_040_000.0, KerbalEndState.Aboard);
            return inputs.Build();
        }

        private static KerbalsWindowUI.KerbalsViewModel BuildReservedStandInRoster()
        {
            // Both the owner AND the first stand-in are reserved, so the SECOND chain
            // member is the active occupant and the first reads "Reserved for <owner>".
            var inputs = new RosterInputs()
                .Kerbal("Jebediah Kerman", "Pilot")
                .Kerbal("Lars Kerman", "Pilot")
                .Kerbal("Dilbert Kerman", "Pilot")
                .Slot("Jebediah Kerman", "Pilot", ownerGone: false,
                      "Lars Kerman", "Dilbert Kerman")
                .Reserve("Jebediah Kerman", double.PositiveInfinity, permanent: false)
                .Reserve("Lars Kerman", 4_320_000.0, permanent: false)
                .Segment("Jebediah Kerman", "rec-duna-1", "tree-duna", "Duna Transfer",
                         3_600_000.0, 4_100_000.0, KerbalEndState.Aboard, "Lars Kerman");
            return inputs.Build();
        }

        private static KerbalsWindowUI.KerbalsViewModel BuildTwoDeepChainRoster()
        {
            var inputs = new RosterInputs()
                .Kerbal("Jebediah Kerman", "Pilot")
                .Kerbal("Lars Kerman", "Pilot")
                .Kerbal("Dilbert Kerman", "Pilot", aboard: "Ike 1")
                .Slot("Jebediah Kerman", "Pilot", ownerGone: false,
                      "Lars Kerman", "Dilbert Kerman")
                .Reserve("Jebediah Kerman", double.PositiveInfinity, permanent: false)
                .Reserve("Lars Kerman", double.PositiveInfinity, permanent: false)
                .Segment("Jebediah Kerman", "rec-ike-1", "tree-ike", "Ike Descent",
                         5_400_000.0, 5_500_000.0, KerbalEndState.Aboard, "Dilbert Kerman");
            return inputs.Build();
        }

        private static KerbalsWindowUI.KerbalsViewModel BuildOwnerGoneRoster()
        {
            var inputs = new RosterInputs()
                .Kerbal("Lars Kerman", "Pilot")
                .Kerbal("Dilbert Kerman", "Pilot")
                .Slot("Jebediah Kerman", "Pilot", ownerGone: true,
                      "Lars Kerman", "Dilbert Kerman")
                .Reserve("Jebediah Kerman", double.PositiveInfinity, permanent: true)
                .Segment("Jebediah Kerman", "rec-eve-1", "tree-eve", "Eve Descent",
                         7_200_000.0, 7_260_000.0, KerbalEndState.Dead, "Lars Kerman");
            return inputs.Build();
        }

        private static KerbalsWindowUI.KerbalsViewModel BuildAllStatusRoster()
        {
            var inputs = new RosterInputs()
                // Available: nothing recorded, no slot - drawn under the plain fold row.
                .Kerbal("Bob Kerman", "Scientist")
                // Assigned: aboard something right now.
                .Kerbal("Bill Kerman", "Engineer", aboard: "Station Alpha")
                // Reserved: an open-ended hold from a flight still out there.
                .Kerbal("Valentina Kerman", "Pilot")
                .Reserve("Valentina Kerman", double.PositiveInfinity, permanent: false)
                .Segment("Valentina Kerman", "rec-minmus-2", "tree-minmus2", "Minmus Outpost",
                         1_900_000.0, 2_040_000.0, KerbalEndState.Aboard)
                // StandIn: an active chain occupant over a reserved owner.
                .Kerbal("Jebediah Kerman", "Pilot")
                .Kerbal("Lars Kerman", "Pilot")
                .Slot("Jebediah Kerman", "Pilot", ownerGone: false, "Lars Kerman")
                .Reserve("Jebediah Kerman", double.PositiveInfinity, permanent: false)
                .Segment("Jebediah Kerman", "rec-mun-3", "tree-mun3", "Mun Relay",
                         900_000.0, 1_000_000.0, KerbalEndState.Aboard, "Lars Kerman")
                // Retired: a displaced stand-in whose owner came back.
                .Kerbal("Dilbert Kerman", "Pilot")
                .Retire("Dilbert Kerman")
                // Lost: a permanently gone owner of a second slot.
                .Slot("Gene Kerman", "Engineer", ownerGone: true)
                .Reserve("Gene Kerman", double.PositiveInfinity, permanent: true)
                .Segment("Gene Kerman", "rec-kerbin-1", "tree-kerbin", "Reentry Test",
                         400_000.0, 404_000.0, KerbalEndState.Dead);
            return inputs.Build();
        }

        // ----- the flights states -----

        private enum FlightShape
        {
            Lost,
            StandInCrew,
            MultiSegment,
            MixedBuckets,
            Unknown,
        }

        private static KerbalsWindowUI.KerbalsViewModel BuildFlights(FlightShape shape)
        {
            var inputs = new RosterInputs().Kerbal("Jebediah Kerman", "Pilot");
            switch (shape)
            {
                case FlightShape.Lost:
                    inputs.Slot("Jebediah Kerman", "Pilot", ownerGone: true)
                          .Reserve("Jebediah Kerman", double.PositiveInfinity, permanent: true)
                          .Segment("Jebediah Kerman", "rec-mun-1", "tree-mun", "Mun Landing 1",
                                   180_000.0, 214_400.0, KerbalEndState.Dead);
                    break;

                case FlightShape.StandInCrew:
                    inputs.Kerbal("Lars Kerman", "Pilot")
                          .Slot("Jebediah Kerman", "Pilot", ownerGone: false, "Lars Kerman")
                          .Segment("Jebediah Kerman", "rec-lko-2", "tree-lko2", "LKO Rescue",
                                   96_000.0, 108_000.0, KerbalEndState.Recovered,
                                   "Lars Kerman");
                    break;

                case FlightShape.MultiSegment:
                    inputs.Segment("Jebediah Kerman", "rec-seg-1", "tree-multi",
                                   "Minmus Return", 1_000_000.0, 1_100_000.0,
                                   KerbalEndState.Aboard)
                          .Segment("Jebediah Kerman", "rec-seg-2", "tree-multi",
                                   "Minmus Return", 1_100_000.0, 1_200_000.0,
                                   KerbalEndState.Aboard)
                          .Segment("Jebediah Kerman", "rec-seg-3", "tree-multi",
                                   "Minmus Return", 1_200_000.0, 1_260_000.0,
                                   KerbalEndState.Recovered);
                    break;

                case FlightShape.MixedBuckets:
                    inputs.Segment("Jebediah Kerman", "rec-a", "tree-a", "LKO Shakedown",
                                   90_000.0, 96_000.0, KerbalEndState.Recovered)
                          .Segment("Jebediah Kerman", "rec-b", "tree-b", "Mun Flyby",
                                   120_000.0, 151_000.0, KerbalEndState.Recovered)
                          .Segment("Jebediah Kerman", "rec-c", "tree-c", "Mun Landing 1",
                                   180_000.0, 214_400.0, KerbalEndState.Dead)
                          .Segment("Jebediah Kerman", "rec-d", "tree-d", "Minmus Outpost",
                                   1_900_000.0, 2_040_000.0, KerbalEndState.Aboard)
                          .Segment("Jebediah Kerman", "rec-e", "tree-e", "Kerbin Survey",
                                   2_500_000.0, 2_505_000.0, KerbalEndState.Unknown);
                    break;

                default:
                    inputs.Segment("Jebediah Kerman", "rec-unknown", "tree-unknown",
                                   "Kerbin Survey", 2_500_000.0, 2_505_000.0,
                                   KerbalEndState.Unknown);
                    break;
            }
            return inputs.Build();
        }
    }
}
