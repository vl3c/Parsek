using System;
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
    /// <c>op=find</c> mechanism - and asserts that every string below appears in the
    /// TARGET WINDOW'S OWN subtree of the tree that frame produced.</para>
    ///
    /// <para><b>WHY THE WITNESSES ARE DERIVED AND NEVER TYPED.</b> Each one is read off
    /// the PAYLOAD, through the same pure per-column formatter the draw method calls. So a
    /// witness is a string that can only be in the tree because the window drew THIS
    /// model: it is not a constant a stale draw could coincidentally match, and it cannot
    /// drift from the cell the formatter renders, because it IS that call.</para>
    ///
    /// <para><b>AND WHY THE COVERED ROW COMES FIRST.</b> A generic "first row" scan
    /// produced witness sets that missed the state's point entirely - seven Structure
    /// states witnessed only their launch row, and six Career contracts states shared one
    /// identical set - so a state could pass its read-back while the cell it exists to
    /// photograph was absent. <see cref="Covered"/> derives from the row that carries the
    /// state's own <c>Covers</c> branch and is tried FIRST; <see cref="Expected"/> only
    /// tops up from the generic scan afterwards. A state with a non-empty <c>Covers</c>
    /// whose covered derivation is empty is refused by the catalogue unit suite.</para>
    ///
    /// <para>Capped at <see cref="MaxWitnesses"/> per state. More would be a longer
    /// conjunction with no more discriminating power, and every extra term is another
    /// chance for a legitimately clipped or folded cell to fail a good capture. The
    /// applier additionally requires every witness to be ABSENT from a PRE-APPLY capture
    /// of the same window, which is what turns "this string is plausible" into "only the
    /// mock put it there".</para>
    /// </summary>
    internal static class GuiMockWitness
    {
        /// <summary>How many derived strings one apply asserts, at most.</summary>
        internal const int MaxWitnesses = 3;

        /// <summary>
        /// The shortest string that can serve as a witness.
        ///
        /// <para>TWO characters, and the Facilities tab is why: its Level cell is
        /// <c>"L2"</c> / <c>"L3"</c>, the shortest discriminating cell in the program. A
        /// one-character floor would let the shared <c>-</c> placeholder through, which
        /// matches half the cells in any window.</para>
        /// </summary>
        internal const int MinWitnessLength = 2;

        /// <summary>The Level cell of an un-upgraded facility. A row reading exactly this
        /// with an empty status is day-one state, which an UNMOCKED window draws too, so
        /// it cannot witness a swap.</summary>
        private const string DayOneFacilityLevel = "L1";

        // The window tab tokens this deriver switches on, named once.
        private const string FlightsTab = "outcomes";
        private const string ContractsTab = "contracts";
        private const string StrategiesTab = "strategies";
        private const string FacilitiesTab = "facilities";
        private const string MilestonesTab = "milestones";

        /// <summary>
        /// The derived witness set for a built payload: the covered-row derivations first,
        /// then the generic scan up to the cap. Empty means the payload carries no
        /// drawable row at all, which the catalogue unit suite refuses outright - a state
        /// with no witness would make <c>mock-not-applied</c> vacuous for exactly that
        /// state.
        /// </summary>
        internal static List<string> Expected(GuiMockPayload payload, string tab,
                                              IReadOnlyList<string> covers)
        {
            List<string> found = Covered(payload, tab, covers);
            AppendGeneric(payload, tab, found);
            return found;
        }

        /// <summary>
        /// The witnesses derived from the rows that carry the state's own <c>Covers</c>
        /// branch keys, in <c>Covers</c> order. Empty when the state claims none.
        ///
        /// <para>This is the half that makes a witness set say something about the state.
        /// It is exposed separately so a unit cell can assert it is non-empty for every
        /// state that claims a branch, rather than trusting that the combined set happened
        /// to include one.</para>
        /// </summary>
        internal static List<string> Covered(GuiMockPayload payload, string tab,
                                             IReadOnlyList<string> covers)
        {
            var found = new List<string>();
            if (payload == null || covers == null) return found;
            for (int i = 0; i < covers.Count && found.Count < MaxWitnesses; i++)
            {
                string cover = covers[i];
                if (string.IsNullOrEmpty(cover)) continue;
                int dot = cover.IndexOf('.');
                if (dot <= 0 || dot >= cover.Length - 1) continue;
                string enumName = cover.Substring(0, dot);
                string member = cover.Substring(dot + 1);

                if (payload.Kerbals.HasValue)
                {
                    AppendKerbalsCovered(payload.Kerbals.Value, tab, enumName, member,
                                         found);
                    AppendKerbalsBoolCovered(payload.Kerbals.Value, tab, cover, found);
                }
                if (payload.Career.HasValue)
                {
                    AppendCareerCovered(payload.Career.Value, tab, enumName, member, found);
                    AppendCareerBoolCovered(payload.Career.Value, tab, cover, found);
                }
                if (payload.Structure != null)
                {
                    AppendStructureCovered(payload.Structure, enumName, member, found);
                    AppendStructureBoolCovered(payload.Structure, cover, found);
                }
            }
            return found;
        }

        private static void AppendGeneric(GuiMockPayload payload, string tab,
                                          List<string> into)
        {
            if (payload == null) return;
            if (payload.Kerbals.HasValue) AppendKerbals(payload.Kerbals.Value, tab, into);
            if (payload.Career.HasValue) AppendCareer(payload.Career.Value, tab, into);
            if (payload.Structure != null) AppendStructure(payload.Structure, into);
        }

        // ----- kerbals: covered -----

        private static void AppendKerbalsCovered(
            KerbalsWindowUI.KerbalsViewModel vm, string tab, string enumName, string member,
            List<string> into)
        {
            if (enumName == "RosterStatus" && tab != FlightsTab)
            {
                KerbalsPresentation.RosterStatus want;
                if (!TryParseEnum(out want, member)) return;
                foreach (KerbalsPresentation.RosterRow row in AllRosterRows(vm))
                {
                    if (row.Status != want) continue;
                    // The STATUS cell is what the state is about, and for Lost / Reserved
                    // its hover text only exists because the real builder resolved the
                    // flight that created the loss or the hold.
                    Add(into, row.StatusText);
                    Add(into, row.StatusTooltipText);
                    return;
                }
                return;
            }

            if (enumName == "ChainMemberStatus" && tab != FlightsTab)
            {
                KerbalsWindowUI.ChainMemberStatus want;
                if (!TryParseEnum(out want, member)) return;
                foreach (KerbalsPresentation.RosterRow row in AllRosterRows(vm))
                {
                    if (!row.MemberStatus.HasValue || row.MemberStatus.Value != want) continue;
                    // A stand-in row nested under its owner is told apart by its Name cell
                    // (the tree glyph), so that is what must be witnessed.
                    Add(into, KerbalsWindowUI.FormatRosterNameCell(row));
                    return;
                }
                return;
            }

            if (enumName == "KerbalEndState")
            {
                KerbalEndState want;
                if (!TryParseEnum(out want, member)) return;
                if (tab == FlightsTab)
                {
                    List<KerbalsPresentation.FlightGroup> groups = vm.Flights;
                    if (groups == null) return;
                    for (int g = 0; g < groups.Count; g++)
                    {
                        List<KerbalsPresentation.FlightRow> rows = groups[g].Rows;
                        if (rows == null) continue;
                        for (int r = 0; r < rows.Count; r++)
                        {
                            if (rows[r].EndState != want) continue;
                            Add(into, rows[r].MissionCellText);
                            return;
                        }
                    }
                    return;
                }
                // On the Roster tab an end state shows through the Last flight cell.
                foreach (KerbalsPresentation.RosterRow row in AllRosterRows(vm))
                {
                    if (row.LastFlightText == null) continue;
                    if (row.LastFlightText.IndexOf(
                            KerbalsPresentation.FormatOutcome(want),
                            StringComparison.Ordinal) < 0)
                        continue;
                    Add(into, row.LastFlightText);
                    return;
                }
            }
        }

        // ----- career: covered -----

        private static void AppendCareerCovered(
            CareerStateWindowUI.CareerStateViewModel vm, string tab, string enumName,
            string member, List<string> into)
        {
            if (enumName == "TimelineEndKind")
            {
                AppendTimelineEndCovered(vm, tab, member, into);
                return;
            }
            if (enumName != "GameActionType") return;
            switch (member)
            {
                case "ContractAccept":
                    // A PENDING row is what an Accept past live UT produces, and it is the
                    // half no capture has; prefer it, fall back to a current row.
                    AppendFirstContract(vm.Contracts.ProjectedRows,
                                        r => r.IsPendingAccept, into);
                    AppendFirstContract(vm.Contracts.CurrentRows, r => true, into);
                    return;
                case "ContractComplete":
                case "ContractFail":
                case "ContractCancel":
                    AppendFirstContract(vm.Contracts.CurrentRows,
                                        r => r.IsClosingByTimelineEnd, into);
                    return;
                case "StrategyActivate":
                    AppendFirstStrategy(vm.Strategies.ProjectedRows,
                                        r => r.IsPendingActivate, into);
                    AppendFirstStrategy(vm.Strategies.CurrentRows, r => true, into);
                    return;
                case "StrategyDeactivate":
                    AppendFirstStrategy(vm.Strategies.CurrentRows,
                                        r => r.IsClosingByTimelineEnd, into);
                    return;
                case "FacilityUpgrade":
                    // An upgrade seen from the Contracts / Strategies tabs shows only
                    // through the SLOT COUNTS, which are numbers rather than cells - so
                    // the covered derivation defers to the generic scan there rather than
                    // producing a numeric witness any capture would satisfy.
                    if (tab == ContractsTab || tab == StrategiesTab) return;
                    AppendFirstFacility(
                        vm.Facilities.Rows,
                        r => r.HasUpcomingChange && r.CurrentLevel != r.ProjectedLevel,
                        into);
                    AppendFirstFacility(vm.Facilities.Rows, r => r.CurrentLevel > 1, into);
                    return;
                case "FacilityDestruction":
                    AppendFirstFacility(vm.Facilities.Rows,
                                        r => r.CurrentDestroyed && r.ProjectedDestroyed,
                                        into);
                    return;
                case "FacilityRepair":
                    AppendFirstFacility(vm.Facilities.Rows,
                                        r => r.CurrentDestroyed && !r.ProjectedDestroyed,
                                        into);
                    return;
                case "MilestoneAchievement":
                    AppendFirstMilestone(vm.Milestones.Rows, r => r.IsPendingCredit, into);
                    AppendFirstMilestone(vm.Milestones.Rows, r => true, into);
                    return;
            }
        }

        // The Timeline-end cell IS the branch: its text names the outcome, so it is the
        // witness, taken from the first row (current, then pending) that carries it.
        private static void AppendTimelineEndCovered(
            CareerStateWindowUI.CareerStateViewModel vm, string tab, string member,
            List<string> into)
        {
            CareerStateWindowUI.TimelineEndKind want;
            if (!TryParseEnum(out want, member)) return;
            if (tab == StrategiesTab)
            {
                foreach (List<CareerStateWindowUI.StrategyRow> rows in new[]
                         { vm.Strategies.CurrentRows, vm.Strategies.PendingRows })
                {
                    if (rows == null) continue;
                    for (int i = 0; i < rows.Count; i++)
                    {
                        if (rows[i].EndKind != want) continue;
                        Add(into, CareerStateWindowUI.FormatStrategyRow_TimelineEnd(
                            rows[i], CareerStateWindowUI.FormatDate));
                        return;
                    }
                }
                return;
            }
            if (tab != ContractsTab) return;
            foreach (List<CareerStateWindowUI.ContractRow> rows in new[]
                     { vm.Contracts.CurrentRows, vm.Contracts.PendingRows })
            {
                if (rows == null) continue;
                for (int i = 0; i < rows.Count; i++)
                {
                    if (rows[i].EndKind != want) continue;
                    Add(into, CareerStateWindowUI.FormatContractRow_TimelineEnd(
                        rows[i], CareerStateWindowUI.FormatDate));
                    return;
                }
            }
        }

        private static void AppendFirstContract(
            List<CareerStateWindowUI.ContractRow> rows,
            Func<CareerStateWindowUI.ContractRow, bool> match, List<string> into)
        {
            if (rows == null || into.Count >= MaxWitnesses) return;
            for (int i = 0; i < rows.Count; i++)
            {
                if (!match(rows[i])) continue;
                Add(into, CareerStateWindowUI.FormatContractRow_Title(rows[i]));
                return;
            }
        }

        private static void AppendFirstStrategy(
            List<CareerStateWindowUI.StrategyRow> rows,
            Func<CareerStateWindowUI.StrategyRow, bool> match, List<string> into)
        {
            if (rows == null || into.Count >= MaxWitnesses) return;
            for (int i = 0; i < rows.Count; i++)
            {
                if (!match(rows[i])) continue;
                // The Flow cell has no picture at all in the census, so it is the witness
                // that matters most on this tab.
                Add(into, CareerStateWindowUI.FormatStrategyRow_Flow(rows[i]));
                Add(into, CareerStateWindowUI.FormatStrategyRow_Title(rows[i]));
                return;
            }
        }

        private static void AppendFirstFacility(
            List<CareerStateWindowUI.FacilityRow> rows,
            Func<CareerStateWindowUI.FacilityRow, bool> match, List<string> into)
        {
            if (rows == null || into.Count >= MaxWitnesses) return;
            for (int i = 0; i < rows.Count; i++)
            {
                if (!match(rows[i])) continue;
                Add(into, CareerStateWindowUI.FormatFacilityRow_TimelineEnd(
                    rows[i], true, CareerStateWindowUI.FormatDate));
                Add(into, CareerStateWindowUI.FormatFacilityRow_Level(rows[i]));
                return;
            }
        }

        private static void AppendFirstMilestone(
            List<CareerStateWindowUI.MilestoneRow> rows,
            Func<CareerStateWindowUI.MilestoneRow, bool> match, List<string> into)
        {
            if (rows == null || into.Count >= MaxWitnesses) return;
            for (int i = 0; i < rows.Count; i++)
            {
                if (!match(rows[i])) continue;
                Add(into, CareerStateWindowUI.FormatMilestoneRow_Title(rows[i]));
                Add(into, CareerStateWindowUI.FormatMilestoneRow_Rewards(rows[i]));
                return;
            }
        }

        // ----- structure: covered -----

        private static void AppendStructureCovered(GuiMockStructure structure,
                                                   string enumName, string member,
                                                   List<string> into)
        {
            if (enumName != "TerminalState") return;
            TerminalState want;
            if (!TryParseEnum(out want, member)) return;
            string status = MissionCompositionBuilder.TerminalName(want);
            List<StructureStep> steps = structure.Steps;
            if (steps == null) return;
            for (int i = 0; i < steps.Count; i++)
            {
                if (!string.Equals(steps[i].Status, status, StringComparison.Ordinal))
                    continue;
                // The terminal word lives in the STATUS column - the Event cell is the
                // generic "End" - so that is what a TerminalState cover must witness.
                Add(into, steps[i].Status);
                Add(into, steps[i].Location);
                return;
            }
        }

        // ----- the bool-driven branches -----
        //
        // Reflection cannot see a branch decided by a bool, and several of the most
        // consequential cells in these windows are: the divergence banner, (pending),
        // (closing), (destroyed), (upcoming), a status tooltip, a collapsed run. Each is a
        // NAMED cover key (see GuiMockCompletenessTests.BoolBranches), and each derives
        // its witness from the row that carries the flag.

        private static void AppendKerbalsBoolCovered(
            KerbalsWindowUI.KerbalsViewModel vm, string tab, string cover, List<string> into)
        {
            switch (cover)
            {
                case "RosterRow.StatusTooltip":
                    if (tab == FlightsTab) return;
                    foreach (KerbalsPresentation.RosterRow row in AllRosterRows(vm))
                    {
                        if (string.IsNullOrEmpty(row.StatusTooltipText)) continue;
                        // The TOOLTIP is the cell this branch exists for, and the dump
                        // carries it as its own key - so it is the witness rather than the
                        // shortened status text beside it.
                        Add(into, row.StatusTooltipText);
                        return;
                    }
                    return;
                case "RosterRow.Chain":
                    // Two or more stand-in rows under one owner: the MID-branch glyph only
                    // a chain that deep draws.
                    if (tab == FlightsTab) return;
                    foreach (KerbalsPresentation.RosterRow row in AllRosterRows(vm))
                    {
                        if (row.Depth < 1 || row.IsLastInSlot) continue;
                        Add(into, KerbalsWindowUI.FormatRosterNameCell(row));
                        return;
                    }
                    return;
                case "FlightRow.MultiSegment":
                    if (tab != FlightsTab) return;
                    foreach (KerbalsPresentation.FlightRow row in AllFlightRows(vm))
                    {
                        if (row.SegmentCount < 2) continue;
                        // The collapsed-segment detail lives in the row's HOVER text since
                        // the 2026-09-15 mission collapse, which is the only place it
                        // still exists.
                        Add(into, row.SegmentSummaryText);
                        return;
                    }
                    return;
                case "FlightRow.CrewNote":
                    if (tab != FlightsTab) return;
                    foreach (KerbalsPresentation.FlightRow row in AllFlightRows(vm))
                    {
                        if (string.IsNullOrEmpty(row.StandInName)) continue;
                        Add(into, row.MissionCellText);
                        return;
                    }
                    return;
            }
        }

        private static void AppendCareerBoolCovered(
            CareerStateWindowUI.CareerStateViewModel vm, string tab, string cover,
            List<string> into)
        {
            switch (cover)
            {
                case "CareerBanner.Divergent":
                    if (!vm.HasDivergence) return;
                    // The banner itself is composed inside the draw method from the VM's
                    // own UT text, so the witness is the PENDING row the divergence
                    // produces - the half of the layout that only exists when it diverges.
                    AppendFirstContract(vm.Contracts.ProjectedRows,
                                        r => r.IsPendingAccept, into);
                    return;
                case "ContractRow.IsPendingAccept":
                    AppendFirstContract(vm.Contracts.ProjectedRows,
                                        r => r.IsPendingAccept, into);
                    return;
                case "ContractRow.IsClosingByTimelineEnd":
                    AppendFirstContract(vm.Contracts.CurrentRows,
                                        r => r.IsClosingByTimelineEnd, into);
                    return;
                case "StrategyRow.IsPendingActivate":
                    AppendFirstStrategy(vm.Strategies.ProjectedRows,
                                        r => r.IsPendingActivate, into);
                    return;
                case "StrategyRow.IsClosingByTimelineEnd":
                    AppendFirstStrategy(vm.Strategies.CurrentRows,
                                        r => r.IsClosingByTimelineEnd, into);
                    return;
                case "FacilityRow.HasUpcomingChange":
                    AppendFirstFacility(
                        vm.Facilities.Rows,
                        r => r.HasUpcomingChange && r.CurrentLevel != r.ProjectedLevel,
                        into);
                    return;
                case "FacilityRow.CurrentDestroyed":
                    AppendFirstFacility(vm.Facilities.Rows,
                                        r => r.CurrentDestroyed && r.ProjectedDestroyed,
                                        into);
                    return;
                case "FacilityRow.DestroyedInTimeline":
                    AppendFirstFacility(vm.Facilities.Rows,
                                        r => !r.CurrentDestroyed && r.ProjectedDestroyed,
                                        into);
                    return;
                case "FacilityRow.RepairedInTimeline":
                    AppendFirstFacility(vm.Facilities.Rows,
                                        r => r.CurrentDestroyed && !r.ProjectedDestroyed,
                                        into);
                    return;
                case "MilestoneRow.IsPendingCredit":
                    AppendFirstMilestone(vm.Milestones.Rows, r => r.IsPendingCredit, into);
                    return;
                case "MilestoneRow.ZeroReward":
                    AppendFirstMilestone(
                        vm.Milestones.Rows,
                        r => r.FundsAwarded == 0f && r.RepAwarded == 0f
                             && r.ScienceAwarded == 0f,
                        into);
                    return;
            }
        }

        private static void AppendStructureBoolCovered(GuiMockStructure structure,
                                                        string cover, List<string> into)
        {
            List<StructureStep> steps = structure.Steps;
            if (steps == null) return;
            switch (cover)
            {
                case "StructureStep.CollapsedRun":
                    for (int i = 0; i < steps.Count; i++)
                    {
                        if (steps[i].Label == null) continue;
                        if (steps[i].Label.IndexOf(" x", StringComparison.Ordinal) < 0)
                            continue;
                        Add(into, steps[i].Label);
                        return;
                    }
                    return;
                case "StructureStep.RouteOrigin":
                    for (int i = 0; i < steps.Count; i++)
                    {
                        if (steps[i].Kind != StructureStepKind.Origin) continue;
                        Add(into, steps[i].Label);
                        Add(into, steps[i].Location);
                        return;
                    }
                    return;
            }
        }

        private static IEnumerable<KerbalsPresentation.FlightRow> AllFlightRows(
            KerbalsWindowUI.KerbalsViewModel vm)
        {
            List<KerbalsPresentation.FlightGroup> groups = vm.Flights;
            if (groups == null) yield break;
            for (int g = 0; g < groups.Count; g++)
            {
                List<KerbalsPresentation.FlightRow> rows = groups[g].Rows;
                if (rows == null) continue;
                for (int r = 0; r < rows.Count; r++) yield return rows[r];
            }
        }

        // ----- kerbals: generic -----

        private static IEnumerable<KerbalsPresentation.RosterRow> AllRosterRows(
            KerbalsWindowUI.KerbalsViewModel vm)
        {
            KerbalsPresentation.RosterRowSet set = vm.Roster;
            if (set.Involved != null)
                for (int i = 0; i < set.Involved.Count; i++) yield return set.Involved[i];
            if (set.Plain != null)
                for (int i = 0; i < set.Plain.Count; i++) yield return set.Plain[i];
        }

        private static void AppendKerbals(KerbalsWindowUI.KerbalsViewModel vm, string tab,
                                          List<string> into)
        {
            // The Flights tab draws NOTHING from the roster and vice versa, so the tab
            // decides which half can witness. `outcomes` is the Flights tab's wire token.
            if (tab == FlightsTab)
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
                        Add(into, rows[r].MissionCellText);
                }
                return;
            }

            AppendRosterRows(vm.Roster.Involved, into);
            if (into.Count < MaxWitnesses) AppendRosterRows(vm.Roster.Plain, into);
        }

        private static void AppendRosterRows(List<KerbalsPresentation.RosterRow> rows,
                                             List<string> into)
        {
            if (rows == null) return;
            for (int i = 0; i < rows.Count && into.Count < MaxWitnesses; i++)
            {
                // StatusText and LastFlightText are drawn verbatim as their own cells
                // (DrawRosterRow). The Name cell is not used: it is the one cell every
                // real roster shares with a mocked one (the stock kerbal names).
                Add(into, rows[i].StatusText);
                Add(into, rows[i].LastFlightText);
            }
        }

        // ----- career: generic -----

        private static void AppendCareer(CareerStateWindowUI.CareerStateViewModel vm,
                                         string tab, List<string> into)
        {
            if (tab == StrategiesTab)
            {
                AppendStrategyRows(vm.Strategies.CurrentRows, into);
                if (into.Count < MaxWitnesses)
                    AppendStrategyRows(vm.Strategies.ProjectedRows, into);
                return;
            }
            if (tab == FacilitiesTab)
            {
                List<CareerStateWindowUI.FacilityRow> rows = vm.Facilities.Rows;
                if (rows == null) return;
                for (int i = 0; i < rows.Count && into.Count < MaxWitnesses; i++)
                {
                    // The LEVEL and TIMELINE-END cells are the discriminating ones: EVERY
                    // facility row draws a title (the tab emits the whole nine-building
                    // inventory even on day one), so a title would match an unmocked
                    // window exactly. A row that reads plain "L1" with no timeline change
                    // is day-one state and is skipped for the same reason.
                    string level = CareerStateWindowUI.FormatFacilityRow_Level(rows[i]);
                    string end = CareerStateWindowUI.FormatFacilityRow_TimelineEnd(
                        rows[i], true, CareerStateWindowUI.FormatDate);
                    if (level == DayOneFacilityLevel && string.IsNullOrEmpty(end))
                        continue;
                    Add(into, level);
                    Add(into, end);
                }
                return;
            }
            if (tab == MilestonesTab)
            {
                List<CareerStateWindowUI.MilestoneRow> rows = vm.Milestones.Rows;
                if (rows == null) return;
                for (int i = 0; i < rows.Count && into.Count < MaxWitnesses; i++)
                {
                    Add(into, CareerStateWindowUI.FormatMilestoneRow_Title(rows[i]));
                    Add(into, CareerStateWindowUI.FormatMilestoneRow_Rewards(rows[i]));
                }
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
                Add(into, CareerStateWindowUI.FormatStrategyRow_Flow(rows[i]));
                Add(into, CareerStateWindowUI.FormatStrategyRow_Title(rows[i]));
            }
        }

        // ----- structure: generic -----

        private static void AppendStructure(GuiMockStructure structure, List<string> into)
        {
            List<StructureStep> steps = structure.Steps;
            if (steps == null) return;
            // BACKWARDS, because what makes a structure state distinctive is its ENDING -
            // a forward scan witnessed only the launch row, which every mission run draws,
            // and that was one of the review's findings. The Status cell leads for the same
            // reason: a launch row's "Prelaunch" is the least discriminating string in the
            // window.
            for (int i = steps.Count - 1; i >= 0 && into.Count < MaxWitnesses; i--)
            {
                Add(into, steps[i].Status);
                Add(into, steps[i].Label);
            }
        }

        // ----- shared -----

        private static bool TryParseEnum<T>(out T value, string member) where T : struct
        {
            value = default(T);
            if (string.IsNullOrEmpty(member)) return false;
            if (!Enum.IsDefined(typeof(T), member)) return false;
            value = (T)Enum.Parse(typeof(T), member);
            return true;
        }

        /// <summary>
        /// Adds a candidate when it is a usable witness: non-empty, not the <c>-</c>
        /// placeholder every column shares, at least <see cref="MinWitnessLength"/>
        /// characters, NOT a bare number, and not already in the set.
        ///
        /// <para>The bare-number exclusion is not decoration - the doc claimed it before
        /// the code did it, which the review caught. A UT, a level number, a reward and a
        /// deadline are all numbers a real window draws every frame, so a numeric witness
        /// would be satisfied by any capture of the same window. A candidate counts as a
        /// number when it contains no letter at all, which also drops <c>"--"</c> and
        /// <c>"0.0%"</c>.</para>
        /// </summary>
        private static void Add(List<string> into, string candidate)
        {
            if (into.Count >= MaxWitnesses) return;
            if (string.IsNullOrEmpty(candidate)) return;
            if (candidate == KerbalsPresentation.EmptyCell) return;
            string trimmed = candidate.Trim();
            if (trimmed.Length < MinWitnessLength) return;
            if (!HasLetter(trimmed)) return;
            for (int i = 0; i < into.Count; i++)
                if (string.Equals(into[i], candidate, StringComparison.Ordinal))
                    return;
            into.Add(candidate);
        }

        private static bool HasLetter(string text)
        {
            for (int i = 0; i < text.Length; i++)
                if (char.IsLetter(text[i])) return true;
            return false;
        }
    }
}
