using System.Collections.Generic;

namespace Parsek.UI.Gallery
{
    /// <summary>
    /// The strings a mocked window MUST have drawn for the apply to count as applied.
    ///
    /// <para><b>WHY THIS EXISTS, and it is the load-bearing part of the whole op.</b> The
    /// obvious read-back - "is the injection member still the object I just wrote?" - is
    /// the vacuous read-back the <c>op=rect</c> settle was first written with and had to
    /// be fixed for: it compares a field with the value just written to it and cannot fail
    /// on any input. Only the DRAW witnesses a data swap. So the applier arms
    /// <c>GuiTreeRecorder.ArmForNextRepaint(label, writeToDisk: false)</c> - the
    /// <c>op=find</c> mechanism - and asserts that every string below appears in the tree
    /// the frame produced.</para>
    ///
    /// <para><b>WHY THE WITNESSES ARE DERIVED AND NEVER TYPED.</b> Each one is read off
    /// the PAYLOAD, through the same pure per-column formatter the draw method calls. So a
    /// witness is a string that can only be in the tree because the window drew THIS
    /// model: it is not a constant a stale draw could coincidentally match, and it cannot
    /// drift from the cell the formatter renders, because it IS that call.</para>
    ///
    /// <para>Capped at <see cref="MaxWitnesses"/> per state. More would be a longer
    /// conjunction with no more discriminating power, and every extra term is another
    /// chance for a legitimately clipped or folded cell to fail a good capture.</para>
    /// </summary>
    internal static class GuiMockWitness
    {
        /// <summary>How many derived strings one apply asserts, at most.</summary>
        internal const int MaxWitnesses = 3;

        /// <summary>
        /// The shortest string that can serve as a witness.
        ///
        /// <para>TWO characters, and the Facilities tab is why: its Level cell is
        /// <c>"L2"</c> / <c>"L3"</c>, the shortest discriminating cell in the program, and
        /// every capture the census has taken reads nine uniform <c>"L1"</c>s - so
        /// <c>"L3"</c> is a string only a mocked (or genuinely upgraded) career draws. A
        /// one-character floor would let the shared <c>-</c> placeholder through, which
        /// matches half the cells in any window.</para>
        /// </summary>
        internal const int MinWitnessLength = 2;

        /// <summary>The Level cell of an un-upgraded facility. A row reading exactly this
        /// with an empty status is day-one state, which an UNMOCKED window draws too, so
        /// it cannot witness a swap.</summary>
        private const string DayOneFacilityLevel = "L1";

        /// <summary>
        /// The derived witness set for a built payload, in derivation order. Empty means
        /// the payload carries no drawable row at all, which the catalogue unit suite
        /// refuses outright: a state with no witness would make <c>mock-not-applied</c>
        /// vacuous for exactly that state.
        /// </summary>
        internal static List<string> Expected(GuiMockPayload payload, string tab)
        {
            var found = new List<string>();
            if (payload == null) return found;
            if (payload.Kerbals.HasValue) AppendKerbals(payload.Kerbals.Value, tab, found);
            if (payload.Career.HasValue) AppendCareer(payload.Career.Value, tab, found);
            if (payload.Structure != null) AppendStructure(payload.Structure, found);
            return found;
        }

        // ----- kerbals -----

        private static void AppendKerbals(KerbalsWindowUI.KerbalsViewModel vm, string tab,
                                          List<string> into)
        {
            // The Flights tab draws NOTHING from the roster and vice versa, so the tab
            // decides which half can witness. `outcomes` is the Flights tab's wire token.
            if (tab == "outcomes")
            {
                List<KerbalsPresentation.FlightGroup> groups = vm.Flights;
                if (groups == null) return;
                for (int g = 0; g < groups.Count && into.Count < MaxWitnesses; g++)
                {
                    // The group header draws as "<arrow> " + HeaderText, so the header
                    // text itself is a substring of a drawn node rather than a whole one -
                    // which is why the tree match is a CONTAINS (see the applier).
                    Add(into, groups[g].HeaderText);
                    List<KerbalsPresentation.FlightRow> rows = groups[g].Rows;
                    if (rows == null) continue;
                    for (int r = 0; r < rows.Count && into.Count < MaxWitnesses; r++)
                    {
                        Add(into, rows[r].MissionText);
                        Add(into, rows[r].CrewNoteText);
                    }
                }
                return;
            }

            KerbalsPresentation.RosterRowSet set = vm.Roster;
            AppendRosterRows(set.Involved, into);
            if (into.Count < MaxWitnesses) AppendRosterRows(set.Plain, into);
        }

        private static void AppendRosterRows(List<KerbalsPresentation.RosterRow> rows,
                                             List<string> into)
        {
            if (rows == null) return;
            for (int i = 0; i < rows.Count && into.Count < MaxWitnesses; i++)
            {
                // StatusText and LastFlightText are drawn verbatim as their own cells
                // (DrawRosterRow); the Name cell is prefixed by a fold arrow or two
                // spaces, so it is deliberately not used here.
                Add(into, rows[i].StatusText);
                Add(into, rows[i].LastFlightText);
            }
        }

        // ----- career -----

        private static void AppendCareer(CareerStateWindowUI.CareerStateViewModel vm,
                                         string tab, List<string> into)
        {
            if (tab == "strategies")
            {
                AppendStrategyRows(vm.Strategies.CurrentRows, into);
                if (into.Count < MaxWitnesses)
                    AppendStrategyRows(vm.Strategies.ProjectedRows, into);
                return;
            }
            if (tab == "facilities")
            {
                List<CareerStateWindowUI.FacilityRow> rows = vm.Facilities.Rows;
                if (rows == null) return;
                for (int i = 0; i < rows.Count && into.Count < MaxWitnesses; i++)
                {
                    // The LEVEL and STATUS cells are the discriminating ones: EVERY
                    // facility row draws a title (the tab emits the whole nine-building
                    // inventory even on day one), so a title would match an unmocked
                    // window exactly. A row that reads plain "L1" with an empty status is
                    // day-one state and is skipped for the same reason.
                    string level = CareerStateWindowUI.FormatFacilityRow_Level(rows[i]);
                    string status = CareerStateWindowUI.FormatFacilityRow_Status(rows[i]);
                    if (level == DayOneFacilityLevel && string.IsNullOrEmpty(status))
                        continue;
                    Add(into, level);
                    Add(into, status);
                }
                return;
            }
            if (tab == "milestones")
            {
                List<CareerStateWindowUI.MilestoneRow> rows = vm.Milestones.Rows;
                if (rows == null) return;
                for (int i = 0; i < rows.Count && into.Count < MaxWitnesses; i++)
                    Add(into, CareerStateWindowUI.FormatMilestoneRow_Title(rows[i]));
                return;
            }

            AppendContractRows(vm.Contracts.CurrentRows, into);
            if (into.Count < MaxWitnesses)
                AppendContractRows(vm.Contracts.ProjectedRows, into);
        }

        private static void AppendContractRows(List<CareerStateWindowUI.ContractRow> rows,
                                               List<string> into)
        {
            if (rows == null) return;
            for (int i = 0; i < rows.Count && into.Count < MaxWitnesses; i++)
                Add(into, CareerStateWindowUI.FormatContractRow_Title(rows[i]));
        }

        private static void AppendStrategyRows(List<CareerStateWindowUI.StrategyRow> rows,
                                               List<string> into)
        {
            if (rows == null) return;
            for (int i = 0; i < rows.Count && into.Count < MaxWitnesses; i++)
            {
                // The Flow cell has no picture at all in the census, so it is the
                // witness that matters most on this tab.
                Add(into, CareerStateWindowUI.FormatStrategyRow_Flow(rows[i]));
                Add(into, CareerStateWindowUI.FormatStrategyRow_Title(rows[i]));
            }
        }

        // ----- structure -----

        private static void AppendStructure(GuiMockStructure structure, List<string> into)
        {
            List<StructureStep> steps = structure.Steps;
            if (steps == null) return;
            for (int i = 0; i < steps.Count && into.Count < MaxWitnesses; i++)
            {
                Add(into, steps[i].Label);
                Add(into, steps[i].Status);
            }
        }

        // ----- shared -----

        /// <summary>
        /// Adds a candidate when it is a usable witness: non-empty, not the
        /// <c>-</c> placeholder every column shares, not a bare number, and not already
        /// in the set. Those exclusions are what keep the conjunction discriminating - a
        /// witness of <c>-</c> would match half the cells in any window.
        /// </summary>
        private static void Add(List<string> into, string candidate)
        {
            if (into.Count >= MaxWitnesses) return;
            if (string.IsNullOrEmpty(candidate)) return;
            if (candidate == KerbalsPresentation.EmptyCell) return;
            if (candidate.Trim().Length < MinWitnessLength) return;
            for (int i = 0; i < into.Count; i++)
                if (string.Equals(into[i], candidate, System.StringComparison.Ordinal))
                    return;
            into.Add(candidate);
        }
    }
}
