using System;
using System.Collections.Generic;
using System.Globalization;

namespace Parsek
{
    /// <summary>
    /// Pure presentation derivations for the Kerbals window's two column tables. No
    /// Unity and no KSP call lives here: the window gathers the live inputs (the stock
    /// roster, the ledger's slots / reservations / retired set, the ERS recordings'
    /// crew end states, the replacement map and the mission names) and hands them over
    /// as plain data, so every row text, every status word, the fold partition and both
    /// sort orders are unit-testable outside IMGUI.
    ///
    /// <para>Two builders, one per tab: <see cref="BuildFlightRows"/> and
    /// <see cref="BuildRosterRows"/>. The roster builder READS the flight rows (a
    /// kerbal's "Last flight" cell and its Lost / Reserved "Since" date come from them),
    /// so the window builds flights first.</para>
    ///
    /// <para>Dates arrive through a <c>Func&lt;double, string&gt;</c> rather than being
    /// formatted here, because the calendar formatter is
    /// <c>KSPUtil.PrintDateCompact</c> - a KSP call. The window passes
    /// <see cref="KerbalsWindowUI.FormatRowDate"/>, which is the Timeline's own
    /// try/catch-with-F0-fallback shape; tests pass a lambda.</para>
    /// </summary>
    internal static class KerbalsPresentation
    {
        /// <summary>The placeholder a cell with nothing to say renders. One constant
        /// because three columns share it and a test pins the spelling.</summary>
        internal const string EmptyCell = "-";

        /// <summary>What a roster row says about a kerbal RIGHT NOW. The order is the
        /// resolution priority: a lost kerbal is never also reported as reserved, a
        /// retired stand-in never as a stand-in, and so on down the list.</summary>
        internal enum RosterStatus
        {
            Lost,
            Retired,
            Reserved,
            StandIn,
            Assigned,
            Available
        }

        /// <summary>One stock-roster kerbal as the window gathered it: the identity and
        /// the one live fact the ledger cannot answer (which vessel he is aboard).</summary>
        internal struct RosterKerbal
        {
            public string Name;
            public string Trait;
            /// <summary>The vessel this kerbal is crewing right now, or null / empty when
            /// he is not aboard anything. Ghost-map ProtoVessels are excluded at the
            /// gather site.</summary>
            public string AssignedVesselName;
        }

        /// <summary>One row of the Roster tab.</summary>
        internal struct RosterRow
        {
            public string Name;
            public string Trait;
            public RosterStatus Status;
            /// <summary>The "Status now" cell.</summary>
            public string StatusText;
            /// <summary>The "Since" cell: the calendar date the current status started,
            /// or <see cref="EmptyCell"/>.</summary>
            public string SinceText;
            /// <summary>The "Last flight" cell: mission name plus outcome word, or
            /// <see cref="EmptyCell"/>.</summary>
            public string LastFlightText;
            /// <summary>The slot this row belongs to: the kerbal himself on an owner row,
            /// the covered owner on a stand-in row, null when the kerbal has no slot.</summary>
            public string SlotOwnerName;
            /// <summary>The slot's replacement chain, for the expandable chain view.
            /// Never null; empty when the row has no slot or an empty chain.</summary>
            public List<KerbalsWindowUI.ChainMember> Chain;
            /// <summary>Whether this kerbal owns at least one recorded flight.</summary>
            public bool HasFlights;
            /// <summary>The name the chain view and the expand seam key this row by.
            /// The kerbal's own name - unique within the roster by construction.</summary>
            public string FoldKey { get { return Name; } }
        }

        /// <summary>The Roster tab's two partitions (the "minimal, need-to-know" ruling):
        /// rows with Parsek involvement or a recorded flight are listed normally, plain
        /// available kerbals with no history collapse under one fold row.</summary>
        internal struct RosterRowSet
        {
            public List<RosterRow> Involved;
            public List<RosterRow> Plain;
        }

        /// <summary>One row of the Flights tab.</summary>
        internal struct FlightRow
        {
            /// <summary>The OWNER the flight is filed under - the reverse-map already ran
            /// in <c>KerbalsModule.PopulateCrewEndStates</c>, so a stand-in's flight files
            /// under the kerbal he covered and says so in <see cref="CrewNoteText"/>.</summary>
            public string KerbalName;
            public double EndUT;
            public string DateText;
            public string MissionText;
            public string OutcomeText;
            /// <summary>"as &lt;stand-in&gt;" when someone else actually flew it, else
            /// <see cref="EmptyCell"/>.</summary>
            public string CrewNoteText;
            public string RecordingId;
            public string RecordingName;
            public KerbalEndState EndState;
        }

        /// <summary>One kerbal's flights, in the order the tab draws them.</summary>
        internal struct FlightGroup
        {
            public string KerbalName;
            public string Trait;
            /// <summary>The always-visible fold header text (the bucket summary).</summary>
            public string HeaderText;
            public List<FlightRow> Rows;
            public string FoldKey { get { return KerbalName; } }
        }

        // ------------------------- the Flights tab -------------------------

        /// <summary>
        /// Builds the Flights tab's grouped row model.
        /// </summary>
        /// <param name="endStates">The per-recording crew end states, as
        /// <c>KerbalsWindowUI.Build</c> collected them off the ERS recordings.</param>
        /// <param name="missionNameByRecordingId">Recording id -> the mission name that
        /// recording belongs to. A recording absent from the map falls back to its own
        /// recorded vessel name.</param>
        /// <param name="rawCrewByRecordingId">Recording id -> the names actually recorded
        /// aboard. The per-flight stand-in source; see
        /// <see cref="ResolveStandIn"/>.</param>
        /// <param name="replacements">Owner -> current stand-in, the persisted
        /// <c>CrewReservationManager.CrewReplacements</c> map. The FALLBACK source only.</param>
        /// <param name="slots">The ledger's slots, so a raw crew name is only read as a
        /// stand-in when it really is one of the owner's chain members.</param>
        /// <param name="traitOf">Owner name -> trait, for the group headers.</param>
        /// <param name="formatDate">Calendar formatter (see the class remarks).</param>
        internal static List<FlightGroup> BuildFlightRows(
            IReadOnlyList<KerbalsWindowUI.CrewEndStateEntry> endStates,
            IReadOnlyDictionary<string, string> missionNameByRecordingId,
            IReadOnlyDictionary<string, IReadOnlyCollection<string>> rawCrewByRecordingId,
            IReadOnlyDictionary<string, string> replacements,
            IReadOnlyDictionary<string, KerbalsModule.KerbalSlot> slots,
            IReadOnlyDictionary<string, string> traitOf,
            Func<double, string> formatDate)
        {
            var groups = new List<FlightGroup>();
            if (endStates == null || endStates.Count == 0) return groups;

            // The window hands these over already sorted (kerbal name ordinal, then EndUT
            // ascending), but the grouping walk below is a run-length walk over that
            // order, so sorting here as well makes the builder correct for any caller.
            var sorted = new List<KerbalsWindowUI.CrewEndStateEntry>(endStates);
            sorted.Sort((a, b) =>
            {
                int n = StringComparer.Ordinal.Compare(a.KerbalName, b.KerbalName);
                if (n != 0) return n;
                return a.EndUT.CompareTo(b.EndUT);
            });

            int i = 0;
            while (i < sorted.Count)
            {
                string name = sorted[i].KerbalName;
                int j = i;
                var rows = new List<FlightRow>();
                while (j < sorted.Count
                       && string.Equals(sorted[j].KerbalName, name, StringComparison.Ordinal))
                {
                    var e = sorted[j];
                    rows.Add(new FlightRow
                    {
                        KerbalName = name,
                        EndUT = e.EndUT,
                        DateText = FormatDateCell(e.EndUT, formatDate),
                        MissionText = ResolveMissionName(e, missionNameByRecordingId),
                        OutcomeText = FormatOutcome(e.EndState),
                        CrewNoteText = FormatCrewNote(
                            ResolveStandIn(name, e.RecordingId, rawCrewByRecordingId,
                                replacements, slots)),
                        RecordingId = e.RecordingId ?? "",
                        RecordingName = e.RecordingName ?? "",
                        EndState = e.EndState
                    });
                    j++;
                }

                string trait = null;
                if (traitOf != null) traitOf.TryGetValue(name, out trait);
                groups.Add(new FlightGroup
                {
                    KerbalName = name,
                    Trait = trait ?? "",
                    HeaderText = FormatFlightGroupHeader(name, trait, rows),
                    Rows = rows
                });
                i = j;
            }

            // Kerbals by name: the run-length walk above already produced that order.
            return groups;
        }

        /// <summary>
        /// The always-visible fold header: <c>"Name [Trait] - N flights: n recovered,
        /// n lost, n aboard, n unknown"</c>. Zero buckets are omitted (the retired
        /// <c>FormatKerbalSummary</c>'s rule, kept), and a single flight reads
        /// "1 flight". A kerbal with no known trait drops the bracket rather than
        /// rendering an empty one.
        /// </summary>
        internal static string FormatFlightGroupHeader(
            string kerbalName, string trait, IReadOnlyList<FlightRow> rows)
        {
            var ic = CultureInfo.InvariantCulture;
            int recovered = 0, lost = 0, aboard = 0, unknown = 0;
            int total = rows == null ? 0 : rows.Count;
            for (int k = 0; k < total; k++)
            {
                switch (rows[k].EndState)
                {
                    case KerbalEndState.Recovered: recovered++; break;
                    case KerbalEndState.Dead: lost++; break;
                    case KerbalEndState.Aboard: aboard++; break;
                    default: unknown++; break;
                }
            }
            var parts = new List<string>(4);
            if (recovered > 0) parts.Add(recovered.ToString(ic) + " recovered");
            if (lost > 0) parts.Add(lost.ToString(ic) + " lost");
            if (aboard > 0) parts.Add(aboard.ToString(ic) + " aboard");
            if (unknown > 0) parts.Add(unknown.ToString(ic) + " unknown");

            string who = string.IsNullOrEmpty(trait)
                ? kerbalName
                : kerbalName + " [" + trait + "]";
            string flightLabel = total == 1 ? "1 flight" : total.ToString(ic) + " flights";
            if (parts.Count == 0) return who + " - " + flightLabel;
            return who + " - " + flightLabel + ": " + string.Join(", ", parts);
        }

        /// <summary>The Flights tab's outcome vocabulary, four words wide.</summary>
        internal static string FormatOutcome(KerbalEndState state)
        {
            switch (state)
            {
                case KerbalEndState.Recovered: return "Recovered";
                case KerbalEndState.Dead: return "Lost";
                case KerbalEndState.Aboard: return "Still aboard";
                default: return "Outcome unknown";
            }
        }

        private static string FormatDateCell(double ut, Func<double, string> formatDate)
        {
            if (formatDate == null)
                return ut.ToString("F0", CultureInfo.InvariantCulture);
            string text = formatDate(ut);
            return string.IsNullOrEmpty(text) ? EmptyCell : text;
        }

        private static string ResolveMissionName(
            KerbalsWindowUI.CrewEndStateEntry e,
            IReadOnlyDictionary<string, string> missionNameByRecordingId)
        {
            string mission = null;
            if (missionNameByRecordingId != null && !string.IsNullOrEmpty(e.RecordingId))
                missionNameByRecordingId.TryGetValue(e.RecordingId, out mission);
            if (!string.IsNullOrEmpty(mission)) return mission;
            if (!string.IsNullOrEmpty(e.RecordingName)) return e.RecordingName;
            return "(unnamed)";
        }

        /// <summary>
        /// Who actually flew one recording in an owner's seat, or null when the owner
        /// flew it himself.
        ///
        /// <para>PRIMARY source: the recording's OWN raw crew. That is per-flight truth -
        /// the names the recorder wrote into that flight's snapshot - and it is the only
        /// source that can distinguish "Lars covered Jeb on this flight" from "Lars
        /// covers Jeb right now". A raw name counts only when it is one of the owner's
        /// chain members, so an unrelated crewmate on a multi-seat flight is never read
        /// as a stand-in.</para>
        ///
        /// <para>FALLBACK: the persisted replacement map, used ONLY when the recording has
        /// no raw-crew entry at all (the load-time sweep can null a recording's
        /// <c>VesselSnapshot</c>, which is where the raw crew comes from). It answers the
        /// CURRENT stand-in, so on an old flight it can name the wrong one; that is a
        /// strictly better answer than silence, and the column reads "as &lt;name&gt;"
        /// either way.</para>
        /// </summary>
        internal static string ResolveStandIn(
            string ownerName,
            string recordingId,
            IReadOnlyDictionary<string, IReadOnlyCollection<string>> rawCrewByRecordingId,
            IReadOnlyDictionary<string, string> replacements,
            IReadOnlyDictionary<string, KerbalsModule.KerbalSlot> slots)
        {
            if (string.IsNullOrEmpty(ownerName)) return null;

            var chain = new HashSet<string>(StringComparer.Ordinal);
            KerbalsModule.KerbalSlot slot = null;
            if (slots != null) slots.TryGetValue(ownerName, out slot);
            if (slot != null && slot.Chain != null)
            {
                for (int i = 0; i < slot.Chain.Count; i++)
                    if (!string.IsNullOrEmpty(slot.Chain[i])) chain.Add(slot.Chain[i]);
            }
            if (chain.Count == 0) return null;

            IReadOnlyCollection<string> rawCrew = null;
            if (rawCrewByRecordingId != null && !string.IsNullOrEmpty(recordingId))
                rawCrewByRecordingId.TryGetValue(recordingId, out rawCrew);

            if (rawCrew != null && rawCrew.Count > 0)
            {
                // The owner himself aboard means no stand-in, whatever else flew along.
                foreach (string raw in rawCrew)
                    if (string.Equals(raw, ownerName, StringComparison.Ordinal)) return null;
                foreach (string raw in rawCrew)
                    if (chain.Contains(raw)) return raw;
                return null;
            }

            string replacement;
            if (replacements != null
                && replacements.TryGetValue(ownerName, out replacement)
                && !string.IsNullOrEmpty(replacement)
                && chain.Contains(replacement))
            {
                return replacement;
            }
            return null;
        }

        private static string FormatCrewNote(string standInName)
        {
            return string.IsNullOrEmpty(standInName) ? EmptyCell : "as " + standInName;
        }

        // ------------------------- the Roster tab -------------------------

        /// <summary>
        /// Builds the Roster tab's row model: one row per stock-roster kerbal the player
        /// can see, plus one row per Parsek stand-in or retiree the stock list does not
        /// carry, partitioned into the normally-listed set and the folded plain set.
        /// </summary>
        /// <param name="roster">The stock roster as gathered (see
        /// <see cref="RosterKerbal"/>). Kerbals Parsek created that the stock list no
        /// longer carries are added from the slots / retired set below.</param>
        /// <param name="flights">The Flights tab's groups, for the "Last flight" and
        /// "Since" cells. May be null.</param>
        internal static RosterRowSet BuildRosterRows(
            IReadOnlyList<RosterKerbal> roster,
            IReadOnlyDictionary<string, KerbalsModule.KerbalSlot> slots,
            IReadOnlyDictionary<string, KerbalsModule.KerbalReservation> reservations,
            IReadOnlyList<string> retired,
            IReadOnlyList<FlightGroup> flights,
            KerbalsWindowUI.ActiveChainIndexFunc activeChainIndexOf,
            Func<double, string> formatDate)
        {
            var set = new RosterRowSet
            {
                Involved = new List<RosterRow>(),
                Plain = new List<RosterRow>()
            };

            var retiredSet = new HashSet<string>(StringComparer.Ordinal);
            if (retired != null)
            {
                for (int i = 0; i < retired.Count; i++)
                    if (!string.IsNullOrEmpty(retired[i])) retiredSet.Add(retired[i]);
            }

            // name -> the slot it belongs to, for owners AND chain members, plus the
            // chain view each row shows.
            var slotOwnerOf = new Dictionary<string, string>(StringComparer.Ordinal);
            var chainOf = new Dictionary<string, List<KerbalsWindowUI.ChainMember>>(
                StringComparer.Ordinal);
            if (slots != null)
            {
                var owners = new List<string>(slots.Keys);
                owners.Sort(StringComparer.Ordinal);
                for (int i = 0; i < owners.Count; i++)
                {
                    string owner = owners[i];
                    KerbalsModule.KerbalSlot slot = slots[owner];
                    if (slot == null) continue;
                    List<KerbalsWindowUI.ChainMember> chain = BuildChainMembers(
                        slot, retiredSet, activeChainIndexOf);
                    slotOwnerOf[owner] = owner;
                    chainOf[owner] = chain;
                    for (int c = 0; c < chain.Count; c++)
                    {
                        string member = chain[c].Name;
                        // First slot wins: a name in two chains is not expected by
                        // construction and duplicate visibility beats losing the link.
                        if (!slotOwnerOf.ContainsKey(member))
                        {
                            slotOwnerOf[member] = owner;
                            chainOf[member] = chain;
                        }
                    }
                }
            }

            // Every name that needs a row: the gathered roster, plus every slot owner,
            // chain member and retiree the roster does not carry (Parsek-created
            // stand-ins and retirees, which the stock list can have dropped).
            var names = new List<string>();
            var seen = new HashSet<string>(StringComparer.Ordinal);
            var traitOf = new Dictionary<string, string>(StringComparer.Ordinal);
            var assignedVesselOf = new Dictionary<string, string>(StringComparer.Ordinal);
            if (roster != null)
            {
                for (int i = 0; i < roster.Count; i++)
                {
                    RosterKerbal k = roster[i];
                    if (string.IsNullOrEmpty(k.Name) || !seen.Add(k.Name)) continue;
                    names.Add(k.Name);
                    traitOf[k.Name] = k.Trait ?? "";
                    if (!string.IsNullOrEmpty(k.AssignedVesselName))
                        assignedVesselOf[k.Name] = k.AssignedVesselName;
                }
            }
            foreach (var pair in slotOwnerOf)
            {
                if (seen.Add(pair.Key)) names.Add(pair.Key);
            }
            foreach (string name in retiredSet)
            {
                if (seen.Add(name)) names.Add(name);
            }
            names.Sort(StringComparer.Ordinal);

            // Per-kerbal flight facts.
            var lastFlightOf = new Dictionary<string, FlightRow>(StringComparer.Ordinal);
            var deathFlightOf = new Dictionary<string, FlightRow>(StringComparer.Ordinal);
            if (flights != null)
            {
                for (int g = 0; g < flights.Count; g++)
                {
                    FlightGroup group = flights[g];
                    if (group.Rows == null || group.Rows.Count == 0) continue;
                    lastFlightOf[group.KerbalName] = group.Rows[group.Rows.Count - 1];
                    for (int r = group.Rows.Count - 1; r >= 0; r--)
                    {
                        if (group.Rows[r].EndState == KerbalEndState.Dead)
                        {
                            deathFlightOf[group.KerbalName] = group.Rows[r];
                            break;
                        }
                    }
                }
            }

            for (int i = 0; i < names.Count; i++)
            {
                string name = names[i];
                string slotOwner;
                slotOwnerOf.TryGetValue(name, out slotOwner);
                bool isOwnerRow = slotOwner != null
                    && string.Equals(slotOwner, name, StringComparison.Ordinal);

                KerbalsModule.KerbalSlot ownSlot = null;
                if (isOwnerRow && slots != null) slots.TryGetValue(name, out ownSlot);

                KerbalsModule.KerbalReservation reservation = null;
                if (reservations != null) reservations.TryGetValue(name, out reservation);

                string assignedVessel;
                assignedVesselOf.TryGetValue(name, out assignedVessel);

                bool hasFlights = lastFlightOf.ContainsKey(name);

                RosterStatus status = ClassifyStatus(
                    name,
                    ownerPermanentlyGone: ownSlot != null && ownSlot.OwnerPermanentlyGone,
                    retired: retiredSet.Contains(name),
                    reservation: reservation,
                    isStandIn: slotOwner != null && !isOwnerRow,
                    assignedVesselName: assignedVessel);

                List<KerbalsWindowUI.ChainMember> chain;
                if (!chainOf.TryGetValue(name, out chain) || chain == null)
                    chain = new List<KerbalsWindowUI.ChainMember>();

                var row = new RosterRow
                {
                    Name = name,
                    Trait = traitOf.ContainsKey(name)
                        ? traitOf[name]
                        : (ownSlot != null ? (ownSlot.OwnerTrait ?? "") : ""),
                    Status = status,
                    StatusText = FormatStatus(status, name, slotOwner, reservation,
                        assignedVessel, formatDate),
                    SinceText = FormatSince(status, name, lastFlightOf, deathFlightOf),
                    LastFlightText = FormatLastFlight(name, lastFlightOf),
                    SlotOwnerName = slotOwner,
                    Chain = chain,
                    HasFlights = hasFlights
                };

                if (IsPlainRow(row)) set.Plain.Add(row);
                else set.Involved.Add(row);
            }

            return set;
        }

        /// <summary>A row with nothing Parsek has to say: available, no slot, no recorded
        /// flight. These are the rows the fold hides (the "minimal, need-to-know"
        /// ruling). An ASSIGNED kerbal stays listed even with no history - "aboard
        /// something right now" is the one stock fact the window is asked for.</summary>
        internal static bool IsPlainRow(RosterRow row)
        {
            return row.Status == RosterStatus.Available
                   && !row.HasFlights
                   && row.SlotOwnerName == null;
        }

        /// <summary>The fold row over the plain partition.</summary>
        internal static string FormatPlainFoldHeader(int count)
        {
            return "Available, no recorded flights ("
                   + count.ToString(CultureInfo.InvariantCulture) + ")";
        }

        internal static RosterStatus ClassifyStatus(
            string name,
            bool ownerPermanentlyGone,
            bool retired,
            KerbalsModule.KerbalReservation reservation,
            bool isStandIn,
            string assignedVesselName)
        {
            if (ownerPermanentlyGone) return RosterStatus.Lost;
            if (reservation != null && reservation.IsPermanent) return RosterStatus.Lost;
            if (retired) return RosterStatus.Retired;
            if (reservation != null) return RosterStatus.Reserved;
            if (isStandIn) return RosterStatus.StandIn;
            if (!string.IsNullOrEmpty(assignedVesselName)) return RosterStatus.Assigned;
            return RosterStatus.Available;
        }

        /// <summary>
        /// The "Status now" cell.
        ///
        /// <para>The reserved form names the slot it serves only when that is SOMEONE
        /// ELSE: a reserved slot owner is reserved for his own return, so "Reserved for
        /// Jebediah Kerman" on Jeb's own row would say nothing the Name column does not.
        /// A reserved stand-in does carry the owner, which is the case the reading is
        /// for.</para>
        /// </summary>
        internal static string FormatStatus(
            RosterStatus status,
            string name,
            string slotOwnerName,
            KerbalsModule.KerbalReservation reservation,
            string assignedVesselName,
            Func<double, string> formatDate)
        {
            switch (status)
            {
                case RosterStatus.Lost:
                    return "Lost";
                case RosterStatus.Retired:
                    return "Retired";
                case RosterStatus.Reserved:
                {
                    double until = reservation == null
                        ? double.PositiveInfinity
                        : reservation.ReservedUntilUT;
                    string tail = double.IsPositiveInfinity(until)
                        ? "until recovery"
                        : "until " + FormatDateCell(until, formatDate);
                    bool forSomeoneElse = !string.IsNullOrEmpty(slotOwnerName)
                        && !string.Equals(slotOwnerName, name, StringComparison.Ordinal);
                    return forSomeoneElse
                        ? "Reserved for " + slotOwnerName + " " + tail
                        : "Reserved " + tail;
                }
                case RosterStatus.StandIn:
                    return "Stand-in for " + (slotOwnerName ?? "?");
                case RosterStatus.Assigned:
                    return "Assigned (" + assignedVesselName + ")";
                default:
                    return "Available";
            }
        }

        /// <summary>
        /// The "Since" cell. The mod dates exactly two of the six statuses: a loss is
        /// dated by the flight that produced it, and a reservation by the flight that
        /// created the hold (the kerbal's latest recorded flight). Retired, stand-in,
        /// assigned and available carry no recorded start, so they read
        /// <see cref="EmptyCell"/> rather than a number that would be a guess.
        /// </summary>
        internal static string FormatSince(
            RosterStatus status,
            string name,
            IReadOnlyDictionary<string, FlightRow> lastFlightOf,
            IReadOnlyDictionary<string, FlightRow> deathFlightOf)
        {
            FlightRow row;
            if (status == RosterStatus.Lost
                && deathFlightOf != null && deathFlightOf.TryGetValue(name, out row))
            {
                return row.DateText;
            }
            if ((status == RosterStatus.Lost || status == RosterStatus.Reserved)
                && lastFlightOf != null && lastFlightOf.TryGetValue(name, out row))
            {
                return row.DateText;
            }
            return EmptyCell;
        }

        /// <summary>The "Last flight" cell: the latest flight's mission name and outcome
        /// word, or <see cref="EmptyCell"/>.</summary>
        internal static string FormatLastFlight(
            string name, IReadOnlyDictionary<string, FlightRow> lastFlightOf)
        {
            FlightRow row;
            if (lastFlightOf == null || !lastFlightOf.TryGetValue(name, out row))
                return EmptyCell;
            return row.MissionText + " - " + row.OutcomeText;
        }

        private static List<KerbalsWindowUI.ChainMember> BuildChainMembers(
            KerbalsModule.KerbalSlot slot,
            HashSet<string> retiredSet,
            KerbalsWindowUI.ActiveChainIndexFunc activeChainIndexOf)
        {
            var members = new List<KerbalsWindowUI.ChainMember>();
            if (slot == null || slot.Chain == null) return members;
            int activeIdx = activeChainIndexOf != null ? activeChainIndexOf(slot) : -1;
            for (int c = 0; c < slot.Chain.Count; c++)
            {
                string memberName = slot.Chain[c];
                if (string.IsNullOrEmpty(memberName)) continue;

                // Retired wins before active, as the retired VM did: ComputeRetiredSet
                // only marks !isReserved names, so the two are mutually exclusive by
                // construction and the ordering here is defensive.
                KerbalsWindowUI.ChainMemberStatus memberStatus;
                if (retiredSet != null && retiredSet.Contains(memberName))
                    memberStatus = KerbalsWindowUI.ChainMemberStatus.Retired;
                else if (activeIdx >= 0 && activeIdx < slot.Chain.Count && c == activeIdx)
                    memberStatus = KerbalsWindowUI.ChainMemberStatus.Active;
                else
                    memberStatus = KerbalsWindowUI.ChainMemberStatus.Displaced;

                members.Add(new KerbalsWindowUI.ChainMember
                {
                    Name = memberName,
                    ChainIndex = c,
                    Status = memberStatus
                });
            }
            return members;
        }
    }
}
