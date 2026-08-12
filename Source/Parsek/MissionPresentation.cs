using System.Collections.Generic;
using System.Globalization;
using System.Text;

namespace Parsek
{
    // Presentation-only derivations for the Missions tab (Stage 1 / Tier 1 of
    // docs/dev/research/mission-presentation-ux-analysis-2026-08-12.md). Everything here is a
    // PURE projection of the existing read models (MissionStructure / MissionThroughLineView /
    // MissionCompositionNode) into the strings the window renders: a per-mission summary line
    // (T1.1 / T1.2), delta-phrased interval labels (T1.3), the same-tree dock partner's name
    // (T1.4), the tooltip texts (T1.5), and the loop-conflict screen message (T1.6).
    //
    // Nothing here reads or writes Mission state, playback state, or persistence: the Missions
    // window calls these to decide what to DRAW, and the derivations must stay side-effect free
    // so they can run once per frame per mission from inside an IMGUI draw pass.
    //
    // No Unity / KSP calls: date formatting (KSPUtil.PrintDateCompact) and duration formatting
    // (ParsekTimeFormat) stay at the call site and their RESULTS are passed in as strings, so
    // every rule below is unit-testable headlessly.
    internal static class MissionPresentation
    {
        // Separator + span arrow for the summary line. Built as escapes so the source stays
        // ASCII, the same way MissionsWindowUI builds its caret glyphs and sort arrows
        // (U+00B7 MIDDLE DOT, U+2192 RIGHTWARDS ARROW).
        internal const string SummarySeparator = " \u00b7 ";
        internal const string SummarySpanArrow = " \u2192 ";

        // ---- T1.5 tooltip texts (one place, so the UI and the tests read the same string) ----

        /// <summary>
        /// The include checkbox. This is the F2 fix: the checkbox writes the mission's LOOP-UNIT
        /// membership (Mission.ExcludedIntervalKeys) and does NOT hide a ghost, which is what
        /// every player assumes it does.
        /// </summary>
        internal const string IncludeCheckboxTooltip =
            "Include this segment in the mission's loop unit. Does not hide the ghost - " +
            "playback is controlled per recording (Recordings tab).";

        internal const string LoopToggleTooltip =
            "Loop this mission as one unit. At most one looping mission per recording tree - " +
            "enabling this clears the loop on any mission that shares its recordings.";

        internal const string CloneButtonTooltip =
            "Duplicate this mission as a second selection over the same recordings " +
            "(its own include set, loop period, and Archive flag).";

        internal const string ArchiveCheckboxTooltip =
            "Hide this mission from the list while the Archive filter is on. Does not change " +
            "looping or ghost playback.";

        internal const string WarpToButtonTooltip =
            "Fast-forward the game clock to just before this mission's next launch.";

        internal const string PartnerJourneyTooltip =
            "Include the docked partner's journey (recorded in another mission's tree) in this " +
            "mission's selection and loop. Off until you tick it.";

        internal const string MissionSummaryTooltip =
            "Mission span, duration, vessel and crew counts, how it ended, and the next launch.";

        // The four loop-period cell states (T1.5): one line naming the state the cell is in.
        internal const string PeriodTooltipLoopOff =
            "Loop off - the stored period is shown but nothing relaunches.";
        internal const string PeriodTooltipAuto =
            "Auto period - inherited from Settings > Looping.";
        internal const string PeriodTooltipClamped =
            "Period raised to fit the overlap cap - the mission is longer than the period you asked for.";
        internal const string PeriodTooltipLocked =
            "Period locked to the launch / transfer window - set by physics, not editable.";

        // The two "Next launch" (formerly TTL) state words (T1.5).
        internal const string NextLaunchTooltipNotAligned =
            "Not aligned: this mission is not on a faithful launch schedule (its shape cannot be " +
            "phase-locked, or no loop unit was built for it).";
        internal const string NextLaunchTooltipContinuous =
            "Continuous: the loop has no window to line up with, so it relaunches on its own cadence.";

        // The state words BuildTMinusCellText emits; matched here so the tooltip picker never
        // needs to re-derive the state.
        internal const string NextLaunchTextNotAligned = "not aligned";
        internal const string NextLaunchTextContinuous = "continuous";

        // ===================== T1.1 / T1.2 - the mission summary line =====================

        /// <summary>
        /// The tree-level facts a mission summary line needs. Derived from the tree's read models
        /// only (never from the Mission's selection), so two Missions over one tree share them.
        /// </summary>
        internal struct MissionSummaryFacts
        {
            public bool HasSpan;        // false when no through-line carried a usable UT
            public double StartUT;
            public double EndUT;
            public int VesselCount;     // distinct physical vessels (EVA kerbals and atoms excluded)
            public int CrewCount;       // union of named crew across the tree's legs
            public string TerminalWord; // how the primary (first) root ended ("Landed", ...)
        }

        /// <summary>
        /// Derives the summary facts for one tree: the span across every through-line, the number
        /// of distinct physical vessels in the composition, the union of named crew across the
        /// tree's legs, and the primary root's terminal word. Pure; a null/empty read model yields
        /// <c>HasSpan == false</c> and zero counts.
        /// </summary>
        internal static MissionSummaryFacts ComputeSummaryFacts(
            MissionStructure structure, MissionThroughLineView view,
            List<MissionCompositionNode> roots)
        {
            var facts = new MissionSummaryFacts
            {
                HasSpan = false,
                StartUT = 0.0,
                EndUT = 0.0,
                VesselCount = 0,
                CrewCount = 0,
                TerminalWord = "",
            };

            // Span: earliest through-line start -> latest through-line end. The through-line view
            // is the vessel-level model, so this is the whole mission's wall-clock extent
            // (offshoots included), independent of any interval trim.
            double min = double.PositiveInfinity;
            double max = double.NegativeInfinity;
            if (view != null)
            {
                foreach (var kv in view.ByHeadId)
                {
                    MissionThroughLine tl = kv.Value;
                    if (tl == null)
                        continue;
                    if (!double.IsNaN(tl.StartUT) && tl.StartUT < min) min = tl.StartUT;
                    if (!double.IsNaN(tl.EndUT) && tl.EndUT > max) max = tl.EndUT;
                }
            }
            if (!double.IsInfinity(min) && !double.IsInfinity(max) && max >= min)
            {
                facts.HasSpan = true;
                facts.StartUT = min;
                facts.EndUT = max;
            }

            // Vessels: distinct composition OwnerHeadIds, counting only real vessel intervals -
            // a roster atom is a part, and an EVA-kerbal leg is a person, so neither is a vessel.
            var owners = new HashSet<string>(System.StringComparer.Ordinal);
            if (roots != null)
                for (int i = 0; i < roots.Count; i++)
                    CollectVesselOwners(roots[i], owners);
            facts.VesselCount = owners.Count;

            // Crew: the union of NAMED crew over the tree's legs (a leg's roster is its
            // start-captured crew), plus any EVA kerbal that has its own leg. Names are the only
            // way to union without double-counting a kerbal that rode several legs; when no names
            // were recorded the count falls back to the largest single-leg crew count.
            if (structure != null)
            {
                var names = new HashSet<string>(System.StringComparer.Ordinal);
                int maxUnnamed = 0;
                foreach (var kv in structure.LegsById)
                {
                    MissionLeg leg = kv.Value;
                    if (leg == null)
                        continue;
                    for (int n = 0; n < leg.CrewNames.Count; n++)
                        if (!string.IsNullOrEmpty(leg.CrewNames[n]))
                            names.Add(leg.CrewNames[n]);
                    if (!string.IsNullOrEmpty(leg.EvaCrewName))
                        names.Add(leg.EvaCrewName);
                    if (leg.CrewCount > maxUnnamed)
                        maxUnnamed = leg.CrewCount;
                }
                facts.CrewCount = names.Count > 0 ? names.Count : maxUnnamed;
            }

            // Outcome: the primary (first) root vessel's LAST interval end event. Walking by max
            // EndUT over that vessel's own intervals avoids assuming where the survivor sits in
            // the children list.
            if (roots != null && roots.Count > 0 && roots[0] != null)
                facts.TerminalWord = ResolvePrimaryTerminalWord(roots[0]) ?? "";

            return facts;
        }

        // Distinct physical-vessel owners under a composition node. A roster atom carries no
        // OwnerHeadId; an EVA-kerbal interval labels itself with the kerbal's name (so its
        // VesselName equals its CompositionLabel) and is a person, not a vessel.
        private static void CollectVesselOwners(MissionCompositionNode node, HashSet<string> owners)
        {
            if (node == null)
                return;
            if (!node.IsAtom && !string.IsNullOrEmpty(node.OwnerHeadId) && !IsPersonNode(node))
                owners.Add(node.OwnerHeadId);
            for (int i = 0; i < node.Children.Count; i++)
                CollectVesselOwners(node.Children[i], owners);
        }

        // True for a node whose label IS its own name - a roster atom or an EVA kerbal, both of
        // which render the bare label rather than "Vessel (composition)".
        private static bool IsPersonNode(MissionCompositionNode node)
        {
            return !string.IsNullOrEmpty(node.VesselName)
                && string.Equals(node.VesselName, node.CompositionLabel, System.StringComparison.Ordinal);
        }

        // The end event of the latest-ending interval belonging to the root's OWN vessel.
        private static string ResolvePrimaryTerminalWord(MissionCompositionNode root)
        {
            string owner = root.OwnerHeadId;
            string best = root.EndEvent;
            double bestEnd = root.EndUT;
            WalkOwnerIntervals(root, owner, ref best, ref bestEnd);
            return best;
        }

        private static void WalkOwnerIntervals(
            MissionCompositionNode node, string owner, ref string best, ref double bestEnd)
        {
            for (int i = 0; i < node.Children.Count; i++)
            {
                MissionCompositionNode c = node.Children[i];
                if (c == null)
                    continue;
                if (!c.IsAtom && string.Equals(c.OwnerHeadId, owner, System.StringComparison.Ordinal))
                {
                    if (c.EndUT >= bestEnd)
                    {
                        bestEnd = c.EndUT;
                        best = c.EndEvent;
                    }
                    WalkOwnerIntervals(c, owner, ref best, ref bestEnd);
                }
            }
        }

        /// <summary>
        /// The mission header's summary line: span, duration, vessel / crew counts, outcome, and
        /// the next launch, joined with middle dots. Every piece is omitted when it has no value,
        /// so a mission with nothing but a vessel count still reads cleanly. Pure - the caller
        /// supplies the already-formatted date / duration / countdown strings (KSPUtil /
        /// ParsekTimeFormat live at the call site).
        /// </summary>
        internal static string BuildSummaryLine(
            string startDateText, string endDateText, string durationText,
            int vesselCount, int crewCount, string terminalWord, string nextLaunchText)
        {
            var ic = CultureInfo.InvariantCulture;
            var sb = new StringBuilder();

            bool hasStart = !string.IsNullOrEmpty(startDateText);
            bool hasEnd = !string.IsNullOrEmpty(endDateText);
            if (hasStart && hasEnd)
                Append(sb, startDateText + SummarySpanArrow + endDateText);
            else if (hasStart)
                Append(sb, startDateText);
            else if (hasEnd)
                Append(sb, endDateText);

            if (!string.IsNullOrEmpty(durationText))
                Append(sb, durationText);

            if (vesselCount > 0)
                Append(sb, vesselCount.ToString(ic) + (vesselCount == 1 ? " vessel" : " vessels"));

            if (crewCount > 0)
                Append(sb, crewCount.ToString(ic) + " crew");

            if (!string.IsNullOrEmpty(terminalWord))
                Append(sb, terminalWord);

            if (!string.IsNullOrEmpty(nextLaunchText))
                Append(sb, "Next launch " + nextLaunchText);

            return sb.ToString();
        }

        private static void Append(StringBuilder sb, string piece)
        {
            if (sb.Length > 0)
                sb.Append(SummarySeparator);
            sb.Append(piece);
        }

        /// <summary>
        /// The countdown to put on the summary line (T1.2): the "Next launch" cell text when it
        /// IS a countdown, otherwise nothing. The state words ("not aligned" / "continuous") stay
        /// on the row cell, where their tooltip explains them; repeating them in the header would
        /// read as a mission outcome. Pure.
        /// </summary>
        internal static string SummaryNextLaunchText(string nextLaunchCellText)
        {
            if (string.IsNullOrEmpty(nextLaunchCellText))
                return null;
            return nextLaunchCellText.StartsWith("T-", System.StringComparison.Ordinal)
                ? nextLaunchCellText
                : null;
        }

        // ===================== T1.3 - delta-phrased interval labels =====================

        /// <summary>
        /// The name-cell label for one composition row. The FIRST interval of a vessel (and every
        /// row whose separation partner cannot be named) keeps the historical
        /// <c>"Vessel (composition)"</c> form; a LATER interval that starts at a separation whose
        /// peeled sibling is resolvable leads with what happened instead of repeating the vessel
        /// name for the third time down the staircase:
        /// <c>"after undock: Kerbal X Lander left - (pod x1, crew x2)"</c>. Pure.
        /// </summary>
        internal static string BuildIntervalRowLabel(
            string vesselName, string compositionLabel, bool isFirstInterval,
            string startEvent, string peeledVesselName)
        {
            string fallback =
                (!string.IsNullOrEmpty(vesselName)
                 && !string.Equals(vesselName, compositionLabel, System.StringComparison.Ordinal))
                    ? vesselName + " (" + compositionLabel + ")"
                    : (compositionLabel ?? "");

            if (isFirstInterval || string.IsNullOrEmpty(peeledVesselName))
                return fallback;

            string verb = SeparationVerb(startEvent);
            if (verb == null)
                return fallback;

            return "after " + verb + ": " + peeledVesselName + " left - ("
                + (compositionLabel ?? "") + ")";
        }

        /// <summary>
        /// The delta verb for a separation event word, or null when the event is not a separation
        /// (a dock / board / EVA / launch / terminal boundary keeps the plain label). Mirrors
        /// <see cref="MissionCompositionBuilder.BranchEventName"/>'s separation outputs. Pure.
        /// </summary>
        internal static string SeparationVerb(string startEvent)
        {
            switch (startEvent)
            {
                case "Decoupled": return "decouple";
                case "Undocked": return "undock";
                case "Broke off": return "break-off";
                case "Broke up": return "break-up";
                default: return null;
            }
        }

        /// <summary>
        /// The vessel name of the sibling that peeled off at <paramref name="node"/>'s start: the
        /// piece the interval boundary created. The builder attaches a structural peel to the
        /// interval it separated FROM (steps 6-7 of <see cref="MissionCompositionBuilder"/>), so
        /// the peel is a sibling of this interval under the same parent, starting at the same UT.
        /// Returns null when there is no such sibling (nothing to name - the caller keeps the
        /// plain label). Pure.
        /// </summary>
        internal static string ResolvePeeledSiblingVesselName(
            MissionCompositionNode parent, MissionCompositionNode node)
        {
            if (parent == null || node == null)
                return null;
            for (int i = 0; i < parent.Children.Count; i++)
            {
                MissionCompositionNode c = parent.Children[i];
                if (c == null || ReferenceEquals(c, node))
                    continue;
                if (c.IsAtom || !c.IsSelectable)
                    continue;
                if (string.Equals(c.HeadLegId, node.HeadLegId, System.StringComparison.Ordinal))
                    continue;
                // Another interval of the SAME vessel is the continuation, not a piece that left it
                // (defensive: the builder chains the survivor as the first child).
                if (!string.IsNullOrEmpty(node.OwnerHeadId)
                    && string.Equals(c.OwnerHeadId, node.OwnerHeadId, System.StringComparison.Ordinal))
                    continue;
                // A person (EVA kerbal) leaves the vessel without ending its interval, so it is
                // never the piece a separation boundary peeled.
                if (IsPersonNode(c))
                    continue;
                if (System.Math.Abs(c.StartUT - node.StartUT) > PeelUtEpsilon)
                    continue;
                if (!string.IsNullOrEmpty(c.VesselName))
                    return c.VesselName;
            }
            return null;
        }

        // Both UTs come from the same leg-UT doubles (the peel's clamped origin UT and the
        // survivor interval's start edge), so this only absorbs representation noise - the same
        // rationale as MissionCrossTreeDock.WindowEpsilon.
        private const double PeelUtEpsilon = 1e-3;

        // ===================== T1.4 - naming the same-tree dock partner =====================

        /// <summary>
        /// The Start-event cell text for a same-tree dock / board boundary once the partner is
        /// known: <c>"Docked with Munport Station"</c>. Falls back to the bare event word when no
        /// partner resolved (a single-parent / cross-tree dock keeps today's text). Pure.
        /// </summary>
        internal static string BuildDockPartnerStartEventText(string eventWord, string partnerVesselName)
        {
            if (string.IsNullOrEmpty(eventWord))
                return "";
            return string.IsNullOrEmpty(partnerVesselName)
                ? eventWord
                : eventWord + " with " + partnerVesselName;
        }

        /// <summary>
        /// True for the two merge event words a dock boundary carries.
        /// </summary>
        internal static bool IsDockEventWord(string eventWord)
        {
            return string.Equals(eventWord, "Docked", System.StringComparison.Ordinal)
                || string.Equals(eventWord, "Boarded", System.StringComparison.Ordinal);
        }

        /// <summary>
        /// The OTHER vessel of a same-tree Dock / Board merge, for the interval that begins at
        /// that merge. The merge leg is the through-line member of
        /// <paramref name="ownerHeadId"/> that started at <paramref name="intervalStartUT"/>
        /// through a Dock / Board branch point; a same-tree merge lists TWO branch parents, one of
        /// which is this vessel's own predecessor - the other one is the partner.
        /// <para>Returns null (no partner named, the caller keeps the plain event word) when the
        /// merge leg cannot be found, when the merge has anything other than exactly two parents
        /// (a cross-tree / foreign dock records one), or when the partner leg carries no vessel
        /// name.</para>
        /// Pure.
        /// </summary>
        internal static string ResolveSameTreeDockPartnerVesselName(
            MissionStructure structure, MissionThroughLineView view,
            string ownerHeadId, double intervalStartUT)
        {
            if (structure == null || view == null || string.IsNullOrEmpty(ownerHeadId))
                return null;
            if (!view.ByHeadId.TryGetValue(ownerHeadId, out MissionThroughLine tl) || tl == null)
                return null;

            for (int m = 0; m < tl.MemberLegIds.Count; m++)
            {
                if (!structure.LegsById.TryGetValue(tl.MemberLegIds[m], out MissionLeg leg)
                    || leg == null)
                    continue;
                if (!leg.OriginBranchPointType.HasValue)
                    continue;
                if (leg.OriginBranchPointType.Value != BranchPointType.Dock
                    && leg.OriginBranchPointType.Value != BranchPointType.Board)
                    continue;
                if (System.Math.Abs(leg.StartUT - intervalStartUT) > PeelUtEpsilon)
                    continue;
                // Only a same-tree merge names a partner: two parents, one of them this
                // vessel's own line.
                if (leg.BranchParentIds.Count != 2)
                    return null;
                for (int p = 0; p < leg.BranchParentIds.Count; p++)
                {
                    string parentId = leg.BranchParentIds[p];
                    if (string.IsNullOrEmpty(parentId) || tl.MemberLegIds.Contains(parentId))
                        continue;
                    if (structure.LegsById.TryGetValue(parentId, out MissionLeg partner)
                        && partner != null && !string.IsNullOrEmpty(partner.VesselName))
                        return partner.VesselName;
                }
                return null;
            }
            return null;
        }

        // ===================== T1.5 - the state tooltips =====================

        /// <summary>
        /// The one-line state name for the loop-period cell (T1.5): which of its four states the
        /// player is looking at. Pure.
        /// </summary>
        internal static string BuildPeriodStateTooltip(
            bool loopEnabled, bool locked, bool auto, bool raisedByOverlapCap)
        {
            if (!loopEnabled)
                return PeriodTooltipLoopOff;
            if (locked)
                return PeriodTooltipLocked;
            if (raisedByOverlapCap)
                return PeriodTooltipClamped;
            return auto ? PeriodTooltipAuto : null;
        }

        /// <summary>
        /// The "Next launch" cell tooltip: the state explanation for the two engine words, joined
        /// with any amber reason(s) already carried by the cell. Null when there is nothing to
        /// say (a blank cell, or a plain countdown with no amber). Pure.
        /// </summary>
        internal static string BuildNextLaunchCellTooltip(string cellText, string amberReasons)
        {
            string state = null;
            if (string.Equals(cellText, NextLaunchTextNotAligned, System.StringComparison.Ordinal))
                state = NextLaunchTooltipNotAligned;
            else if (string.Equals(cellText, NextLaunchTextContinuous, System.StringComparison.Ordinal))
                state = NextLaunchTooltipContinuous;

            bool hasAmber = !string.IsNullOrEmpty(amberReasons);
            if (state != null && hasAmber)
                return state + "; " + amberReasons;
            if (state != null)
                return state;
            return hasAmber ? amberReasons : null;
        }

        // ===================== T1.6 - the loop-conflict outcome =====================

        /// <summary>
        /// The screen message for a loop that MOVED: enabling one mission's loop cleared the loop
        /// on the missions named in <paramref name="clearedNames"/> (one loop per recording tree).
        /// Returns null when nothing was cleared, so the caller posts nothing. Pure.
        /// <para><see cref="ParsekLog.ScreenMessage"/> prefixes "[Parsek] ", so the text starts
        /// with the outcome.</para>
        /// </summary>
        internal static string BuildLoopMovedScreenMessage(
            string winnerName, IReadOnlyList<string> clearedNames)
        {
            if (clearedNames == null || clearedNames.Count == 0)
                return null;
            string winner = string.IsNullOrEmpty(winnerName) ? "(mission)" : winnerName;
            var sb = new StringBuilder();
            sb.Append("Loop moved to '").Append(winner).Append("' - one loop per recording tree (");
            for (int i = 0; i < clearedNames.Count; i++)
            {
                if (i > 0)
                    sb.Append(", ");
                string name = string.IsNullOrEmpty(clearedNames[i]) ? "(mission)" : clearedNames[i];
                sb.Append('\'').Append(name).Append('\'');
            }
            sb.Append(" unlooped)");
            return sb.ToString();
        }
    }
}
