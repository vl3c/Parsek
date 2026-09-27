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
    /// kerbal's "Last flight" cell, and the flight named by a Lost / Reserved status and
    /// its hover text, come from them), so the window builds flights first.</para>
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
            /// <summary>True when <see cref="AssignedVesselName"/> is the kerbal's own EVA
            /// vessel (stock <c>Vessel.isEVA</c>). An EVA kerbal IS his own vessel, so the
            /// assigned form would name the kerbal himself; the status reads
            /// <c>On EVA</c> instead.</summary>
            public bool AssignedVesselIsEva;
        }

        /// <summary>
        /// One row of the Roster tab.
        ///
        /// <para>Rows are grouped BY SLOT: a slot owner's row is followed directly by one
        /// row per stand-in of his replacement chain (<see cref="Depth"/> 1), in chain
        /// order, so "who covers whom" is read off the table's shape. That replaced the
        /// per-owner chain fold, whose lines repeated what the stand-in rows already said
        /// (the 2026-09-22 review, recommendation 7).</para>
        /// </summary>
        internal struct RosterRow
        {
            public string Name;
            public string Trait;
            public RosterStatus Status;
            /// <summary>The "Status now" cell.</summary>
            public string StatusText;
            /// <summary>The "Status now" cell's hover text, or null when the cell needs
            /// none (<see cref="FormatStatusTooltip"/>).</summary>
            public string StatusTooltipText;
            /// <summary>The "Last flight" cell: mission name plus outcome word, or
            /// <see cref="EmptyCell"/>.</summary>
            public string LastFlightText;
            /// <summary>The Timeline jump target behind the "Last flight" cell: the latest
            /// mission's last segment, the same recording a Flights row jumps to. Null when
            /// the kerbal has no recorded flight (the cell is then plain text).</summary>
            public string LastFlightRecordingId;
            /// <summary>The slot this row belongs to: the kerbal himself on an owner row,
            /// the covered owner on a stand-in row, null when the kerbal has no slot.</summary>
            public string SlotOwnerName;
            /// <summary>0 for a top-level row; 1 for a stand-in listed under the owner whose
            /// slot he is in (2 for a stand-in of a stand-in who owns a slot himself).</summary>
            public int Depth;
            /// <summary>On a nested row, whether it is the last stand-in under its owner
            /// (picks the tree glyph).</summary>
            public bool IsLastInSlot;
            /// <summary>On a nested row, the stand-in's own place in the chain
            /// (active / retired / displaced); null on a top-level row.</summary>
            public KerbalsWindowUI.ChainMemberStatus? MemberStatus;
            /// <summary>How many stand-in rows are listed directly under this row (owner
            /// rows only; 0 elsewhere).</summary>
            public int SlotMemberCount;
            /// <summary>Whether this kerbal owns at least one recorded flight.</summary>
            public bool HasFlights;
        }

        /// <summary>The Roster tab's two partitions (the "minimal, need-to-know" ruling):
        /// rows with Parsek involvement or a recorded flight are listed normally, plain
        /// available kerbals with no history collapse under one fold row.</summary>
        internal struct RosterRowSet
        {
            public List<RosterRow> Involved;
            public List<RosterRow> Plain;
            /// <summary>How many saved chain members were left out because they no longer
            /// exist (<see cref="IsDeletedStandIn"/>). Logged by the window's VM summary.</summary>
            public int OmittedStandIns;
        }

        /// <summary>
        /// One row of the Flights tab: one MISSION (one recording tree) as one kerbal
        /// experienced it, not one recorded segment.
        ///
        /// <para>The row unit was the segment until 2026-09-15; the GUI-11 capture read
        /// five rows per kerbal for what the player did as two missions, all but one of
        /// them repeating the same date and mission name, one of them
        /// <c>Outcome unknown</c> for a mid-mission segment that simply has no ending. The
        /// segments are still all there - as the count and the ordered outcome list in
        /// <see cref="SegmentSummaryText"/>, which is the row's hover text.</para>
        /// </summary>
        internal struct FlightRow
        {
            /// <summary>The OWNER the flight is filed under - the reverse-map already ran
            /// in <c>KerbalsModule.PopulateCrewEndStates</c>, so a stand-in's flight files
            /// under the kerbal he covered and says so in <see cref="MissionCellText"/>.</summary>
            public string KerbalName;
            /// <summary>The mission key: the tree id, or the recording id when the
            /// recording has no tree (a standalone / pre-tree recording is its own
            /// mission).</summary>
            public string MissionKey;
            /// <summary>The kerbal's FIRST segment start in this mission - what the Date
            /// cell reads.</summary>
            public double StartUT;
            /// <summary>The kerbal's LAST segment end in this mission. Picks the kerbal's
            /// latest flight (the Roster tab's "Last flight" cell) and the row order's tie
            /// break; never drawn.</summary>
            public double EndUT;
            /// <summary>The Date cell: the calendar form of <see cref="StartUT"/>. The
            /// window dates a mission by its launch everywhere - this cell and the Roster
            /// tab's Lost hover text alike - which is how the Missions window's first date
            /// column and the Timeline place a mission too.</summary>
            public string DateText;
            /// <summary>The mission name alone (the Roster tab's "Last flight" cell reads
            /// it).</summary>
            public string MissionText;
            /// <summary>The Mission cell as drawn: <see cref="MissionText"/>, plus
            /// <c>" (flown by &lt;stand-in&gt;)"</c> when someone else actually flew this
            /// kerbal's seat. The rare stand-in note lives here since the 2026-09-22 review
            /// removed the mostly-empty Crew column.</summary>
            public string MissionCellText;
            /// <summary>The FINAL outcome word across the mission's segments.</summary>
            public string OutcomeText;
            /// <summary>Who actually flew it, when a stand-in covered this kerbal's seat;
            /// null when the owner flew it himself.</summary>
            public string StandInName;
            /// <summary>The LAST segment's recording id - the Timeline jump target, so a
            /// click lands on where the mission got to rather than where it started.</summary>
            public string RecordingId;
            /// <summary>The last segment's recorded vessel name (the hover text's raw
            /// identity half).</summary>
            public string RecordingName;
            /// <summary>The FINAL end state across the mission's segments.</summary>
            public KerbalEndState EndState;
            /// <summary>How many recorded segments this mission collapsed.</summary>
            public int SegmentCount;
            /// <summary>The hover text's segment half: <c>"3 segments: Still aboard,
            /// Still aboard, Recovered"</c>, in segment order.</summary>
            public string SegmentSummaryText;
        }

        /// <summary>One kerbal's missions, in the order the tab draws them.</summary>
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
        /// Builds the Flights tab's grouped row model: one group per kerbal, and inside it
        /// ONE ROW PER MISSION (per recording tree the kerbal appears in), not one per
        /// recorded segment.
        ///
        /// <para>Collapse rule, per (kerbal, tree):</para>
        /// <list type="bullet">
        /// <item>Date = the EARLIEST segment start;</item>
        /// <item>Mission = the mission name of any of its segments (they share a tree, so
        /// they share the name; the first resolvable one wins);</item>
        /// <item>Outcome / <c>EndState</c> = the LAST segment by end UT. A segment with no
        /// recorded ending followed by one that has an ending is therefore NOT the answer -
        /// which is the whole reason the segment rows read wrong;</item>
        /// <item>the Timeline jump target = that same last segment's recording;</item>
        /// <item>the segment count and each segment's own outcome word go into the row's
        /// hover text (<see cref="FormatSegmentSummary"/>).</item>
        /// </list>
        ///
        /// <para>A recording with no tree keys by its own recording id, so a standalone
        /// recording - an EVA branch that is its own tree included - stays a row of its
        /// own.</para>
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
                // Mission key -> that mission's segments for THIS kerbal, in end-UT order
                // (the sort above already produced it within a kerbal's run).
                var missionKeys = new List<string>();
                var segmentsOf = new Dictionary<string, List<KerbalsWindowUI.CrewEndStateEntry>>(
                    StringComparer.Ordinal);
                while (j < sorted.Count
                       && string.Equals(sorted[j].KerbalName, name, StringComparison.Ordinal))
                {
                    KerbalsWindowUI.CrewEndStateEntry e = sorted[j];
                    string key = MissionKeyOf(e);
                    List<KerbalsWindowUI.CrewEndStateEntry> bucket;
                    if (!segmentsOf.TryGetValue(key, out bucket))
                    {
                        bucket = new List<KerbalsWindowUI.CrewEndStateEntry>();
                        segmentsOf[key] = bucket;
                        missionKeys.Add(key);
                    }
                    bucket.Add(e);
                    j++;
                }

                var rows = new List<FlightRow>();
                for (int m = 0; m < missionKeys.Count; m++)
                {
                    rows.Add(BuildMissionRow(
                        name,
                        missionKeys[m],
                        segmentsOf[missionKeys[m]],
                        missionNameByRecordingId,
                        rawCrewByRecordingId,
                        replacements,
                        slots,
                        formatDate));
                }

                // Rows read top-down in the order the Date column shows, which is the
                // mission's START. Ties break on the end, so the order is total.
                rows.Sort((a, b) =>
                {
                    int s = a.StartUT.CompareTo(b.StartUT);
                    if (s != 0) return s;
                    return a.EndUT.CompareTo(b.EndUT);
                });

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

        /// <summary>The mission a segment belongs to: its tree, or itself when it has no
        /// tree (a standalone recording is its own mission).</summary>
        internal static string MissionKeyOf(KerbalsWindowUI.CrewEndStateEntry e)
        {
            if (!string.IsNullOrEmpty(e.TreeId)) return e.TreeId;
            return e.RecordingId ?? "";
        }

        private static FlightRow BuildMissionRow(
            string kerbalName,
            string missionKey,
            List<KerbalsWindowUI.CrewEndStateEntry> segments,
            IReadOnlyDictionary<string, string> missionNameByRecordingId,
            IReadOnlyDictionary<string, IReadOnlyCollection<string>> rawCrewByRecordingId,
            IReadOnlyDictionary<string, string> replacements,
            IReadOnlyDictionary<string, KerbalsModule.KerbalSlot> slots,
            Func<double, string> formatDate)
        {
            // Segment order inside one mission is END-UT ascending: that is the order the
            // hover text lists the outcomes in, and its last element is the mission's own
            // outcome. The caller's sort already produced it; sorting again keeps the
            // helper correct for any caller.
            segments.Sort((a, b) => a.EndUT.CompareTo(b.EndUT));

            KerbalsWindowUI.CrewEndStateEntry last = segments[segments.Count - 1];
            double startUT = segments[0].StartUT;
            for (int s = 1; s < segments.Count; s++)
                if (segments[s].StartUT < startUT) startUT = segments[s].StartUT;

            // The mission name is a property of the tree, so any segment answers it - but
            // a recording with no MissionStore entry falls back to its OWN vessel name, so
            // take the first segment that resolves a real mission name and only fall back
            // when none does.
            string mission = null;
            for (int s = 0; s < segments.Count; s++)
            {
                if (missionNameByRecordingId != null
                    && !string.IsNullOrEmpty(segments[s].RecordingId)
                    && missionNameByRecordingId.TryGetValue(segments[s].RecordingId, out mission)
                    && !string.IsNullOrEmpty(mission))
                {
                    break;
                }
                mission = null;
            }
            if (string.IsNullOrEmpty(mission))
                mission = ResolveMissionName(last, missionNameByRecordingId);

            // The crew note answers "did a stand-in fly this MISSION", so the first
            // segment that names one wins - a stand-in who flew the launch and handed
            // over mid-mission still flew it.
            string standIn = null;
            for (int s = 0; s < segments.Count && standIn == null; s++)
            {
                standIn = ResolveStandIn(kerbalName, segments[s].RecordingId,
                    rawCrewByRecordingId, replacements, slots);
            }

            return new FlightRow
            {
                KerbalName = kerbalName,
                MissionKey = missionKey ?? "",
                StartUT = startUT,
                EndUT = last.EndUT,
                DateText = FormatDateCell(startUT, formatDate),
                MissionText = mission,
                MissionCellText = FormatMissionCell(mission, standIn),
                OutcomeText = FormatOutcome(last.EndState),
                StandInName = standIn,
                RecordingId = last.RecordingId ?? "",
                RecordingName = last.RecordingName ?? "",
                EndState = last.EndState,
                SegmentCount = segments.Count,
                SegmentSummaryText = FormatSegmentSummary(segments)
            };
        }

        /// <summary>
        /// The mission row's hover half: <c>"1 segment: Still aboard"</c> /
        /// <c>"3 segments: Still aboard, Still aboard, Recovered"</c>, in end-UT order, so
        /// the detail the segment rows used to show is one hover away rather than gone.
        /// </summary>
        internal static string FormatSegmentSummary(
            IReadOnlyList<KerbalsWindowUI.CrewEndStateEntry> segments)
        {
            int n = segments == null ? 0 : segments.Count;
            if (n == 0) return "";
            var words = new List<string>(n);
            for (int s = 0; s < n; s++) words.Add(FormatOutcome(segments[s].EndState));
            string label = n == 1
                ? "1 segment"
                : n.ToString(CultureInfo.InvariantCulture) + " segments";
            return label + ": " + string.Join(", ", words.ToArray());
        }

        /// <summary>
        /// The always-visible fold header: <c>"Name [Trait] - N missions: n recovered,
        /// n lost, n aboard, n unknown"</c>. It counts MISSIONS, because a mission is what
        /// a row is since 2026-09-15 - a header reading "5 flights" over two rows was the
        /// second half of the same defect. Zero buckets are omitted (the retired
        /// <c>FormatKerbalSummary</c>'s rule, kept), a single mission reads "1 mission",
        /// and the buckets count each mission's FINAL outcome, so a mission whose middle
        /// segment has no ending is not counted as unknown.
        ///
        /// <para>A kerbal with no known trait drops the bracket rather than rendering an
        /// empty one.</para>
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
            string missionLabel = total == 1 ? "1 mission" : total.ToString(ic) + " missions";
            if (parts.Count == 0) return who + " - " + missionLabel;
            return who + " - " + missionLabel + ": " + string.Join(", ", parts);
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
        /// strictly better answer than silence, and the Mission cell reads
        /// "(flown by &lt;name&gt;)" either way.</para>
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

        /// <summary>The Flights tab's Mission cell: the mission name, plus
        /// <c>" (flown by &lt;stand-in&gt;)"</c> when a stand-in flew this kerbal's seat.
        /// The note used to be a Crew column of its own that read <c>-</c> on every row of
        /// every capture; it is rare, so it rides the cell it qualifies.</summary>
        internal static string FormatMissionCell(string missionText, string standInName)
        {
            string mission = missionText ?? "";
            return string.IsNullOrEmpty(standInName)
                ? mission
                : mission + " (flown by " + standInName + ")";
        }

        // ------------------------- the Roster tab -------------------------

        /// <summary>
        /// Builds the Roster tab's row model: one row per stock-roster kerbal the player
        /// can see, plus one row per Parsek stand-in or retiree the stock list does not
        /// carry, partitioned into the normally-listed set and the folded plain set.
        ///
        /// <para><b>Grouped by slot.</b> A slot owner's row is followed directly by his
        /// chain members in chain order (<see cref="RosterRow.Depth"/> 1), so a stand-in
        /// reads under the kerbal he covers instead of rows away in the alphabet.
        /// Everything else sorts by name. A chain member is attached to the FIRST slot
        /// (ordinal owner order) that lists him; a name that is both a chain member and
        /// the owner of a slot of his own (a reserved stand-in gets one) carries his own
        /// members one level deeper.</para>
        ///
        /// <para><b>Deleted stand-ins are not rows.</b> See
        /// <see cref="IsDeletedStandIn"/>.</para>
        /// </summary>
        /// <param name="roster">The stock roster as gathered (see
        /// <see cref="RosterKerbal"/>). Kerbals Parsek created that the stock list no
        /// longer carries are added from the slots / retired set below.</param>
        /// <param name="flights">The Flights tab's groups, for the "Last flight" cell and
        /// the flight a Lost / Reserved status names. May be null.</param>
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

            // The gathered stock roster first: the deleted-stand-in rule below needs to
            // know who the stock list still carries.
            var names = new List<string>();
            var seen = new HashSet<string>(StringComparer.Ordinal);
            var inRoster = new HashSet<string>(StringComparer.Ordinal);
            var traitOf = new Dictionary<string, string>(StringComparer.Ordinal);
            var assignedVesselOf = new Dictionary<string, string>(StringComparer.Ordinal);
            var onEva = new HashSet<string>(StringComparer.Ordinal);
            if (roster != null)
            {
                for (int i = 0; i < roster.Count; i++)
                {
                    RosterKerbal k = roster[i];
                    if (string.IsNullOrEmpty(k.Name) || !seen.Add(k.Name)) continue;
                    names.Add(k.Name);
                    inRoster.Add(k.Name);
                    traitOf[k.Name] = k.Trait ?? "";
                    if (!string.IsNullOrEmpty(k.AssignedVesselName))
                    {
                        assignedVesselOf[k.Name] = k.AssignedVesselName;
                        if (k.AssignedVesselIsEva) onEva.Add(k.Name);
                    }
                }
            }

            // Slots: which owner each chain member is listed under, and his status in
            // that chain. This is what makes a row read "Stand-in for X" - membership
            // alone does not (see ClassifyStatus).
            var memberOwnerOf = new Dictionary<string, string>(StringComparer.Ordinal);
            var memberStatusOf = new Dictionary<string, KerbalsWindowUI.ChainMemberStatus>(
                StringComparer.Ordinal);
            var membersOf = new Dictionary<string, List<string>>(StringComparer.Ordinal);
            var owners = new List<string>();
            if (slots != null)
            {
                owners.AddRange(slots.Keys);
                owners.Sort(StringComparer.Ordinal);
                for (int i = 0; i < owners.Count; i++)
                {
                    string owner = owners[i];
                    KerbalsModule.KerbalSlot slot = slots[owner];
                    if (slot == null) continue;
                    var kept = new List<string>();
                    List<KerbalsWindowUI.ChainMember> chain = BuildChainMembers(
                        slot, retiredSet, activeChainIndexOf);
                    for (int c = 0; c < chain.Count; c++)
                    {
                        string member = chain[c].Name;
                        if (string.Equals(member, owner, StringComparison.Ordinal)) continue;
                        bool reserved = reservations != null && reservations.ContainsKey(member);
                        if (IsDeletedStandIn(chain[c].Status, inRoster.Contains(member),
                                retiredSet.Contains(member), reserved))
                        {
                            set.OmittedStandIns++;
                            continue;
                        }
                        // First slot wins: a name in two chains is not expected by
                        // construction, and one listing beats two.
                        if (memberOwnerOf.ContainsKey(member)) continue;
                        memberOwnerOf[member] = owner;
                        memberStatusOf[member] = chain[c].Status;
                        kept.Add(member);
                    }
                    membersOf[owner] = kept;
                }
            }

            // Every name that needs a row: the gathered roster, plus every slot owner,
            // kept chain member and retiree the roster does not carry.
            for (int i = 0; i < owners.Count; i++)
                if (seen.Add(owners[i])) names.Add(owners[i]);
            foreach (var pair in memberOwnerOf)
                if (seen.Add(pair.Key)) names.Add(pair.Key);
            foreach (string name in retiredSet)
                if (seen.Add(name)) names.Add(name);
            names.Sort(StringComparer.Ordinal);

            // Per-kerbal flight facts.
            var groupOf = new Dictionary<string, FlightGroup>(StringComparer.Ordinal);
            var lastFlightOf = new Dictionary<string, FlightRow>(StringComparer.Ordinal);
            var deathFlightOf = new Dictionary<string, FlightRow>(StringComparer.Ordinal);
            if (flights != null)
            {
                for (int g = 0; g < flights.Count; g++)
                {
                    FlightGroup group = flights[g];
                    if (group.Rows == null || group.Rows.Count == 0) continue;
                    groupOf[group.KerbalName] = group;
                    // "Latest flight" is the latest-ENDING mission, picked explicitly
                    // rather than as the last row: the rows are ordered by the Date column
                    // (the mission START), and two missions can overlap.
                    FlightRow latest = group.Rows[0];
                    FlightRow latestDeath = default(FlightRow);
                    bool haveDeath = false;
                    for (int r = 0; r < group.Rows.Count; r++)
                    {
                        FlightRow row = group.Rows[r];
                        if (row.EndUT > latest.EndUT) latest = row;
                        if (row.EndState != KerbalEndState.Dead) continue;
                        if (!haveDeath || row.EndUT > latestDeath.EndUT)
                        {
                            latestDeath = row;
                            haveDeath = true;
                        }
                    }
                    lastFlightOf[group.KerbalName] = latest;
                    if (haveDeath) deathFlightOf[group.KerbalName] = latestDeath;
                }
            }

            var ctx = new RowContext
            {
                Slots = slots,
                Reservations = reservations,
                RetiredSet = retiredSet,
                TraitOf = traitOf,
                AssignedVesselOf = assignedVesselOf,
                OnEva = onEva,
                MemberOwnerOf = memberOwnerOf,
                MemberStatusOf = memberStatusOf,
                MembersOf = membersOf,
                GroupOf = groupOf,
                LastFlightOf = lastFlightOf,
                DeathFlightOf = deathFlightOf,
                FormatDate = formatDate
            };

            // Top level: every name that is not listed under an owner. Each owner is
            // followed by its members, recursively; the emitted set breaks a cycle.
            var emitted = new HashSet<string>(StringComparer.Ordinal);
            for (int i = 0; i < names.Count; i++)
            {
                if (memberOwnerOf.ContainsKey(names[i])) continue;
                EmitRowWithMembers(names[i], 0, false, ctx, emitted, set);
            }
            // Safety net: a member whose owner chain loops back on itself never reached
            // the top level above; list it rather than lose it.
            for (int i = 0; i < names.Count; i++)
            {
                if (!emitted.Contains(names[i]))
                    EmitRowWithMembers(names[i], 0, false, ctx, emitted, set);
            }

            return set;
        }

        /// <summary>The per-build lookups <see cref="BuildRow"/> reads.</summary>
        private sealed class RowContext
        {
            public IReadOnlyDictionary<string, KerbalsModule.KerbalSlot> Slots;
            public IReadOnlyDictionary<string, KerbalsModule.KerbalReservation> Reservations;
            public HashSet<string> RetiredSet;
            public Dictionary<string, string> TraitOf;
            public Dictionary<string, string> AssignedVesselOf;
            public HashSet<string> OnEva;
            public Dictionary<string, string> MemberOwnerOf;
            public Dictionary<string, KerbalsWindowUI.ChainMemberStatus> MemberStatusOf;
            public Dictionary<string, List<string>> MembersOf;
            public Dictionary<string, FlightGroup> GroupOf;
            public Dictionary<string, FlightRow> LastFlightOf;
            public Dictionary<string, FlightRow> DeathFlightOf;
            public Func<double, string> FormatDate;
        }

        private static void EmitRowWithMembers(
            string name, int depth, bool isLastInSlot, RowContext ctx,
            HashSet<string> emitted, RosterRowSet set)
        {
            if (!emitted.Add(name)) return;
            // Only the members actually attached HERE (first slot wins) are drawn under
            // this row.
            var attached = new List<string>();
            List<string> members;
            if (ctx.MembersOf.TryGetValue(name, out members))
            {
                for (int m = 0; m < members.Count; m++)
                {
                    string owner;
                    if (ctx.MemberOwnerOf.TryGetValue(members[m], out owner)
                        && string.Equals(owner, name, StringComparison.Ordinal)
                        && !emitted.Contains(members[m]))
                    {
                        attached.Add(members[m]);
                    }
                }
            }

            RosterRow row = BuildRow(name, depth, isLastInSlot, attached.Count, ctx);
            if (depth == 0 && IsPlainRow(row)) set.Plain.Add(row);
            else set.Involved.Add(row);

            for (int m = 0; m < attached.Count; m++)
            {
                EmitRowWithMembers(attached[m], depth + 1, m == attached.Count - 1, ctx,
                    emitted, set);
            }
        }

        private static RosterRow BuildRow(
            string name, int depth, bool isLastInSlot, int memberCount, RowContext ctx)
        {
            KerbalsModule.KerbalSlot ownSlot = null;
            if (ctx.Slots != null) ctx.Slots.TryGetValue(name, out ownSlot);

            string coveredOwner;
            ctx.MemberOwnerOf.TryGetValue(name, out coveredOwner);
            string slotOwner = coveredOwner ?? (ownSlot != null ? name : null);

            KerbalsModule.KerbalReservation reservation = null;
            if (ctx.Reservations != null) ctx.Reservations.TryGetValue(name, out reservation);

            string assignedVessel;
            ctx.AssignedVesselOf.TryGetValue(name, out assignedVessel);
            bool eva = ctx.OnEva.Contains(name);

            KerbalsWindowUI.ChainMemberStatus? memberStatus = null;
            KerbalsWindowUI.ChainMemberStatus ms;
            if (coveredOwner != null && ctx.MemberStatusOf.TryGetValue(name, out ms))
                memberStatus = ms;

            RosterStatus status = ClassifyStatus(
                name,
                ownerPermanentlyGone: ownSlot != null && ownSlot.OwnerPermanentlyGone,
                retired: ctx.RetiredSet.Contains(name),
                reservation: reservation,
                memberStatus: memberStatus,
                assignedVesselName: assignedVessel);

            FlightGroup group;
            FlightGroup? ownGroup = ctx.GroupOf.TryGetValue(name, out group)
                ? group
                : (FlightGroup?)null;
            FlightRow? hold = status == RosterStatus.Reserved
                ? ResolveHoldFlight(reservation, ownGroup)
                : null;
            // A Lost kerbal carries a release date only when his death's stock respawn is
            // pending (owner ruling S8): a permanent death has none.
            string releaseDateText = status == RosterStatus.Reserved
                    || (status == RosterStatus.Lost && KerbalsModule.IsRespawnPendingHold(reservation))
                ? FormatReleaseDate(reservation, ctx.FormatDate)
                : null;
            FlightRow deathRow;
            FlightRow? death = ctx.DeathFlightOf.TryGetValue(name, out deathRow)
                ? deathRow
                : (FlightRow?)null;
            FlightRow lastRow;
            bool hasFlights = ctx.LastFlightOf.TryGetValue(name, out lastRow);

            string trait;
            if (!ctx.TraitOf.TryGetValue(name, out trait))
                trait = ownSlot != null ? (ownSlot.OwnerTrait ?? "") : "";

            return new RosterRow
            {
                Name = name,
                Trait = trait,
                Status = status,
                StatusText = FormatStatus(status, name, slotOwner, hold,
                    assignedVessel, eva, releaseDateText),
                StatusTooltipText = FormatStatusTooltip(status, name, slotOwner, hold,
                    death, assignedVessel, eva, releaseDateText),
                LastFlightText = FormatLastFlight(name, ctx.LastFlightOf),
                LastFlightRecordingId = hasFlights && !string.IsNullOrEmpty(lastRow.RecordingId)
                    ? lastRow.RecordingId
                    : null,
                SlotOwnerName = slotOwner,
                Depth = depth,
                IsLastInSlot = depth > 0 && isLastInSlot,
                MemberStatus = depth > 0 ? memberStatus : null,
                SlotMemberCount = memberCount,
                HasFlights = hasFlights
            };
        }

        /// <summary>
        /// A saved chain member who no longer exists as a kerbal: his chain position is
        /// displaced, the stock roster does not carry him, and he is neither retired nor
        /// reserved. That is exactly the set <c>KerbalsModule.ApplyToRoster</c> deletes
        /// ("Stand-in '...' displaced -> deleted (unused)"): the slot keeps the NAME so a
        /// later rewind can reuse it, and the window used to re-add it as an
        /// <c>Available</c> row - telling the player a kerbal was available who is in no
        /// stock list (the c1 capture: Lars Kerman, Jebediah's stand-in after Jebediah
        /// was lost). A displaced member who IS still in the roster (seated on a live
        /// vessel, say) stays listed.
        /// </summary>
        internal static bool IsDeletedStandIn(
            KerbalsWindowUI.ChainMemberStatus memberStatus,
            bool inStockRoster,
            bool retired,
            bool reserved)
        {
            return memberStatus == KerbalsWindowUI.ChainMemberStatus.Displaced
                   && !inStockRoster
                   && !retired
                   && !reserved;
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

        /// <summary>
        /// The "Status now" classification, in the resolution order
        /// <see cref="RosterStatus"/> declares.
        ///
        /// <para><b>Stand-in is a per-MEMBER fact, not a per-chain one.</b> Chain
        /// MEMBERSHIP is what places a row under a slot owner; it is not what makes the
        /// kerbal the one standing in. A chain has at most ONE active occupant, so only
        /// <see cref="KerbalsWindowUI.ChainMemberStatus.Active"/> reads as a stand-in; a
        /// displaced or retired member falls through to <c>Assigned (&lt;vessel&gt;)</c> /
        /// <c>Available</c> like any other kerbal.</para>
        ///
        /// <para>That also fixes the dead-owner case for free: when the owner is
        /// permanently gone <c>KerbalsModule.GetActiveChainIndex</c> answers
        /// <c>NoActiveChainOccupant</c>, so NO member is Active and the freed members stop
        /// being labelled as covering a slot nobody will return to.</para>
        /// </summary>
        /// <param name="memberStatus">This kerbal's status inside the chain of SOMEONE
        /// ELSE's slot, or null when the kerbal is listed under no owner.</param>
        internal static RosterStatus ClassifyStatus(
            string name,
            bool ownerPermanentlyGone,
            bool retired,
            KerbalsModule.KerbalReservation reservation,
            KerbalsWindowUI.ChainMemberStatus? memberStatus,
            string assignedVesselName)
        {
            if (ownerPermanentlyGone) return RosterStatus.Lost;
            if (KerbalsModule.IsLossHold(reservation)) return RosterStatus.Lost;
            if (retired) return RosterStatus.Retired;
            if (reservation != null) return RosterStatus.Reserved;
            if (memberStatus.HasValue
                && memberStatus.Value == KerbalsWindowUI.ChainMemberStatus.Active)
            {
                return RosterStatus.StandIn;
            }
            if (!string.IsNullOrEmpty(assignedVesselName)) return RosterStatus.Assigned;
            return RosterStatus.Available;
        }

        /// <summary>
        /// The recorded flight that holds a reserved kerbal, for the status cell and its
        /// hover text, or null when the kerbal has no flight of his own (a reserved
        /// stand-in: his flights are filed under the owner he covered).
        ///
        /// <para>An open-ended hold (<c>ReservedUntilUT</c> = +inf) comes from a flight
        /// that ends with the kerbal ABOARD (or with no recorded ending), so the latest
        /// mission that ends Still aboard answers it; a finite hold comes from recovered
        /// flights. Either way the latest-ending mission is the fallback.</para>
        /// </summary>
        internal static FlightRow? ResolveHoldFlight(
            KerbalsModule.KerbalReservation reservation, FlightGroup? group)
        {
            if (!group.HasValue || group.Value.Rows == null || group.Value.Rows.Count == 0)
                return null;
            List<FlightRow> rows = group.Value.Rows;
            bool openEnded = reservation == null
                             || double.IsPositiveInfinity(reservation.ReservedUntilUT);

            FlightRow latest = rows[0];
            FlightRow latestAboard = default(FlightRow);
            bool haveAboard = false;
            for (int r = 0; r < rows.Count; r++)
            {
                if (rows[r].EndUT > latest.EndUT) latest = rows[r];
                if (rows[r].EndState != KerbalEndState.Aboard) continue;
                if (!haveAboard || rows[r].EndUT > latestAboard.EndUT)
                {
                    latestAboard = rows[r];
                    haveAboard = true;
                }
            }
            if (openEnded && haveAboard) return latestAboard;
            return latest;
        }

        /// <summary>
        /// The calendar date a FINITE reservation ends at (a Recovered flight's end), or
        /// null for a permanent / open-ended hold, which time alone never releases.
        /// </summary>
        internal static string FormatReleaseDate(
            KerbalsModule.KerbalReservation reservation, Func<double, string> formatDate)
        {
            if (reservation == null || reservation.IsPermanent) return null;
            double until = reservation.ReservedUntilUT;
            if (double.IsNaN(until) || double.IsInfinity(until)) return null;
            return FormatDateCell(until, formatDate);
        }

        /// <summary>
        /// The "Status now" cell.
        ///
        /// <para><b>A finite hold reads its release date; an open-ended one names what
        /// holds the kerbal.</b> A Recovered flight's reservation ends when game time
        /// reaches the flight's recorded end (design 9.3; <c>KerbalReservationReleaseTests</c>
        /// drives the real walk before, at and after that UT), and the window only ever
        /// sees the reservations the LAST walk found in force, so <c>Reserved until
        /// &lt;date&gt;</c> (<paramref name="releaseDateText"/>) is the date the next walk
        /// after it will release him. The window does not re-walk on its own: the Space
        /// Center, the Tracking Station and the crew dialog run a crossed-an-end check,
        /// but in FLIGHT the release lands at the next warp exit, commit or scene change,
        /// so until then the cell can show a date that has just passed. A flight
        /// that ends with the kerbal aboard (or with no recorded ending) holds him with no
        /// end date, so that cell reads <c>Reserved: aboard &lt;vessel&gt;</c> /
        /// <c>Reserved: &lt;mission&gt;</c> and the hover carries the rule.</para>
        ///
        /// <para>The reserved form names the slot it serves only when that is SOMEONE
        /// ELSE: a reserved stand-in reads <c>Reserved for &lt;owner&gt;</c>.</para>
        ///
        /// <para>A long vessel or mission name that does not fit
        /// <see cref="StatusCellMaxChars"/> drops out of the cell (it stays in the hover
        /// text): a clipped cell reads as a shorter status rather than as an
        /// overflow.</para>
        /// </summary>
        internal static string FormatStatus(
            RosterStatus status,
            string name,
            string slotOwnerName,
            FlightRow? hold,
            string assignedVesselName,
            bool assignedVesselIsEva,
            string releaseDateText = null)
        {
            switch (status)
            {
                case RosterStatus.Lost:
                {
                    // A death whose stock respawn is pending names the day he is back.
                    if (string.IsNullOrEmpty(releaseDateText)) return "Lost";
                    string until = "Lost until " + releaseDateText;
                    return until.Length <= StatusCellMaxChars ? until : "Lost";
                }
                case RosterStatus.Retired:
                    return "Retired";
                case RosterStatus.Reserved:
                {
                    if (IsForSomeoneElse(name, slotOwnerName))
                        return "Reserved for " + slotOwnerName;
                    if (!string.IsNullOrEmpty(releaseDateText))
                    {
                        string until = "Reserved until " + releaseDateText;
                        return until.Length <= StatusCellMaxChars ? until : "Reserved";
                    }
                    if (!hold.HasValue) return "Reserved";
                    FlightRow h = hold.Value;
                    if (h.EndState == KerbalEndState.Aboard)
                    {
                        string aboard = "Reserved: aboard " + HoldVesselName(h);
                        return aboard.Length <= StatusCellMaxChars
                            ? aboard
                            : "Reserved: still aboard";
                    }
                    string flight = "Reserved: " + h.MissionText;
                    return flight.Length <= StatusCellMaxChars ? flight : "Reserved";
                }
                case RosterStatus.StandIn:
                {
                    string standIn = "Stand-in for " + (slotOwnerName ?? "?");
                    string where = AboardPhrase(assignedVesselName, assignedVesselIsEva);
                    if (where == null) return standIn;
                    string withVessel = standIn + " (" + where + ")";
                    return withVessel.Length <= StatusCellMaxChars ? withVessel : standIn;
                }
                case RosterStatus.Assigned:
                    return assignedVesselIsEva
                        ? "On EVA"
                        : "Assigned (" + assignedVesselName + ")";
                default:
                    return "Available";
            }
        }

        private static bool IsForSomeoneElse(string name, string slotOwnerName)
        {
            return !string.IsNullOrEmpty(slotOwnerName)
                   && !string.Equals(slotOwnerName, name, StringComparison.Ordinal);
        }

        /// <summary>The vessel a hold names: the last segment's recorded vessel, falling
        /// back to the mission name.</summary>
        private static string HoldVesselName(FlightRow hold)
        {
            return string.IsNullOrEmpty(hold.RecordingName) ? hold.MissionText : hold.RecordingName;
        }

        /// <summary><c>aboard &lt;vessel&gt;</c>, <c>on EVA</c>, or null when the kerbal is
        /// aboard nothing.</summary>
        private static string AboardPhrase(string assignedVesselName, bool isEva)
        {
            if (string.IsNullOrEmpty(assignedVesselName)) return null;
            return isEva ? "on EVA" : "aboard " + assignedVesselName;
        }

        /// <summary>Pessimistic average character advance for the skin's label font, the
        /// same 7 px <c>TooltipEchoBudgetTests</c> budgets its strips at.</summary>
        internal const float StatusCellCharAdvancePx = 7f;

        /// <summary>How many characters the 220 px "Status now" column holds at
        /// <see cref="StatusCellCharAdvancePx"/>. A composed status carries its vessel or
        /// mission name INLINE only when it fits; past that the name moves into the cell's
        /// hover text.</summary>
        internal static int StatusCellMaxChars
        {
            get { return (int)(KerbalsWindowUI.ColW_RosterStatus / StatusCellCharAdvancePx); }
        }

        /// <summary>The release rule an OPEN-ENDED reservation hover ends with (a flight
        /// that ends with the kerbal aboard, or with no recorded ending): no end date
        /// exists, so the walk keeps it at every game time and drops it only when the
        /// flight leaves the committed set (<c>KerbalReservationReleaseTests</c>).</summary>
        internal const string ReservationHoldRule =
            "Passing time does not release it; it lasts while that flight stays in the timeline.";

        /// <summary>The release rule a FINITE reservation hover ends with: the kerbal is
        /// free again once game time reaches the Recovered flight's end.</summary>
        internal static string FormatReservationReleaseRule(string releaseDateText)
        {
            return "Free again from " + releaseDateText + ", when that flight ends.";
        }

        /// <summary>The Lost hover's way back. True because a Re-Fly merge tombstones the
        /// superseded flight's <c>KerbalAssignment</c>+Dead row
        /// (<c>TombstoneEligibility.IsKerbalDeath</c>), and the permanent reservation that
        /// row created is what <see cref="RosterStatus.Lost"/> reads (flown:
        /// <c>CL-3-refly-crew-tombstone</c>, "death-sourced reservation IS
        /// released").</summary>
        internal const string LostReFlyRemedy =
            "If that mission has a rewind point, re-flying it can undo the loss.";

        /// <summary>The release rule a Lost hover carries when the death's stock crew
        /// respawn is pending (owner ruling S8): the respawn policy in force at the death
        /// brings the kerbal back on that date.</summary>
        internal static string FormatLostRespawnRule(string releaseDateText)
        {
            return "Stock respawn returns this kerbal on " + releaseDateText + ".";
        }

        /// <summary>
        /// The "Status now" cell's hover text, or null when the cell says everything
        /// already.
        ///
        /// <list type="bullet">
        /// <item><b>Lost</b>: the mission that killed the kerbal, dated by its launch like
        /// every other date in the window, and the way back
        /// (<see cref="LostReFlyRemedy"/>).</item>
        /// <item><b>Reserved</b>: the flight that holds the kerbal and the release rule
        /// (<see cref="FormatReservationReleaseRule"/> for a finite hold,
        /// <see cref="ReservationHoldRule"/> for an open-ended one).</item>
        /// <item><b>Stand-in</b> aboard a craft whose inline form does not fit
        /// <see cref="StatusCellMaxChars"/>: where he is.</item>
        /// </list>
        /// </summary>
        internal static string FormatStatusTooltip(
            RosterStatus status,
            string name,
            string slotOwnerName,
            FlightRow? hold,
            FlightRow? death,
            string assignedVesselName,
            bool assignedVesselIsEva,
            string releaseDateText = null)
        {
            string rule = string.IsNullOrEmpty(releaseDateText)
                ? ReservationHoldRule
                : FormatReservationReleaseRule(releaseDateText);
            switch (status)
            {
                case RosterStatus.Lost:
                {
                    string respawn = string.IsNullOrEmpty(releaseDateText)
                        ? ""
                        : FormatLostRespawnRule(releaseDateText) + " ";
                    if (!death.HasValue)
                        return "Lost on a committed flight. " + respawn + LostReFlyRemedy;
                    return "Lost on " + death.Value.MissionText + " (launched "
                           + death.Value.DateText + "). " + respawn + LostReFlyRemedy;
                }

                case RosterStatus.Reserved:
                {
                    if (IsForSomeoneElse(name, slotOwnerName))
                        return "Held by a committed flight flown in " + slotOwnerName
                               + "'s seat. " + rule;
                    if (!hold.HasValue)
                        return "Held by a committed flight. " + rule;
                    FlightRow h = hold.Value;
                    string lead = "Held by the committed flight " + h.MissionText;
                    switch (h.EndState)
                    {
                        case KerbalEndState.Aboard:
                            lead += ", which ends with this kerbal aboard " + HoldVesselName(h);
                            break;
                        case KerbalEndState.Recovered:
                            lead += ", which ends with this kerbal recovered";
                            break;
                        case KerbalEndState.Unknown:
                            lead += ", which has no recorded ending";
                            break;
                    }
                    return lead + ". " + rule;
                }

                case RosterStatus.StandIn:
                {
                    string where = AboardPhrase(assignedVesselName, assignedVesselIsEva);
                    if (where == null) return null;
                    string standIn = "Stand-in for " + (slotOwnerName ?? "?");
                    if ((standIn + " (" + where + ")").Length <= StatusCellMaxChars)
                    {
                        // It fits inline, so the cell already carries the vessel.
                        return null;
                    }
                    return "Standing in for " + (slotOwnerName ?? "?") + "; " + where + ".";
                }

                default:
                    return null;
            }
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
