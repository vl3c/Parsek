using System.Collections.Generic;
using Parsek.Logistics;
using UnityEngine;

namespace Parsek
{
    // Foreign dock links: the link set and journey legs, the foreign dock link rows, the recorded dock partner row and the partner journey nodes.
    internal partial class MissionsWindowUI
    {
        // ---- M-MIS-8: cross-tree partner-journey rows ----

        // Derived cross-tree dock links for a mission tree, built at most once per frame per
        // tree (the per-frame cache clears alongside missionViewCache). Derivation suppressed
        // like the other per-frame builders so the Verbose summary does not flood.
        private List<ForeignDockLink> GetForeignDockLinks(RecordingTree tree, List<RecordingTree> trees)
        {
            if (!foreignLinksCache.TryGetValue(tree.Id, out List<ForeignDockLink> links))
            {
                bool prev = MissionCrossTreeDock.SuppressLogging;
                MissionCrossTreeDock.SuppressLogging = true;
                try
                {
                    links = MissionCrossTreeDock.FindLinks(tree, trees);
                }
                finally
                {
                    MissionCrossTreeDock.SuppressLogging = prev;
                }
                foreignLinksCache[tree.Id] = links;
            }
            return links;
        }

        private List<string> GetJourneyLegIds(RecordingTree foreignTree, ForeignDockLink link)
        {
            if (!journeyLegsCache.TryGetValue(link.LinkId, out List<string> legs))
            {
                // Per-frame derivation: suppress the walk's Verbose summary (same rationale as
                // GetForeignDockLinks above).
                bool prev = MissionCrossTreeDock.SuppressLogging;
                MissionCrossTreeDock.SuppressLogging = true;
                try
                {
                    legs = MissionCrossTreeDock.ComputePartnerJourneyLegIds(foreignTree, link);
                }
                finally
                {
                    MissionCrossTreeDock.SuppressLogging = prev;
                }
                journeyLegsCache[link.LinkId] = legs;
            }
            return legs;
        }

        // The Docked partner rows: one row per dock between this mission's vessels and ANOTHER
        // mission's, whichever side recorded it.
        //   - A dock another mission RECORDED ONTO this mission's vessel (a derived foreign link,
        //     MissionCrossTreeDock.FindLinks): "Docked partner: <vessel> (mission '<name>')", the
        //     dock date in the Start time column, an include toggle bound to
        //     Mission.IncludedForeignDockLinkIds (Advanced); when included, the journey's foreign
        //     intervals render as child rows with the normal per-interval checkboxes (same
        //     ExcludedIntervalKeys binding - keys are globally unique).
        //   - A dock THIS mission recorded with another mission's vessel (the digest's own
        //     cross-tree merge rows): the same row shape, no toggle (there is no foreign journey
        //     to pull in - the dock is already this tree's own), drawn by
        //     DrawRecordedDockPartnerRow.
        // Both carry a "Go to" in the Re-Fly slot that opens the partner's mission. Returns the
        // number of rows drawn.
        private int DrawForeignDockLinkRows(Mission mission, RecordingTree tree, List<RecordingTree> trees)
        {
            List<ForeignDockLink> links = GetForeignDockLinks(tree, trees);
            List<MissionEventRow> digest = GetEventDigest(mission, tree);
            List<MissionEventRow> recorded = MissionPresentation.SelectRecordedCrossMissionDocks(digest);
            if (links.Count == 0 && recorded.Count == 0)
                return 0;
            int total = links.Count + recorded.Count;

            // The partner-journey toggle picks which foreign journey the loop replays, so it is
            // part of the same authoring set as the per-interval checkboxes and goes with them in
            // Basic. The ROW itself stays in both modes: it reports that this vessel docked with
            // another tree's, which is mission history, not loop composition. What an include
            // DOES to the row (the dimming while it is off, the journey child rows while it is
            // on) is loop-selection styling, so Basic draws the row plain and without its
            // journey; the include itself is untouched and returns with Advanced.
            bool loopAuthoring = ShowsLoopAuthoringControls(ParsekUI.AppliedUiComplexityMode);

            int rows = 0;
            for (int li = 0; li < links.Count; li++)
            {
                ForeignDockLink link = links[li];
                bool included = mission.IncludedForeignDockLinkIds.Contains(link.LinkId);

                GUILayout.BeginHorizontal(GUILayout.MinHeight(CompositionRowMinHeight));
                GUILayout.Label("", bodyCellLabel, GUILayout.Width(ColW_Enable));
                if (!loopAuthoring)
                {
                    // Same single blank cell the roster-atom rows draw, so the column stays aligned.
                    GUILayout.Label("", bodyCellLabel, GUILayout.Width(ColW_Index));
                }
                else
                {
                    // T1.5: the checkbox both pulls in foreign rows AND can clear a loop on the
                    // linked mission (the spanned tree set widens), so it needs to say what it does.
                    bool toggled = GUILayout.Toggle(included, PartnerJourneyCheckboxContent,
                        GUILayout.Width(ColW_Index), GUILayout.ExpandHeight(true));
                    if (toggled != included)
                    {
                        if (toggled) mission.IncludedForeignDockLinkIds.Add(link.LinkId);
                        else mission.IncludedForeignDockLinkIds.Remove(link.LinkId);
                        ParsekLog.Info("Mission",
                            $"Mission '{mission.Name}' partner journey link={link.LinkId} " +
                            $"(foreign tree={link.ForeignTreeId}, vessel='{link.ForeignVesselName}') " +
                            $"included={toggled}");
                        // Including a link on an ALREADY-LOOPING mission widens its spanned tree
                        // set, which can newly conflict with a loop on the foreign tree; clear the
                        // conflict now (SetLoopEnabled only runs this on loop-enable). This is the
                        // SECOND path that can take another mission's loop, so it gets the same
                        // T1.6 snapshot + on-screen announcement as the Loop toggle - a loop
                        // moving silently here is the exact symptom T1.6 removed.
                        if (toggled && mission.LoopPlayback && !YieldLoopToPartnerRoute(mission, link))
                        {
                            List<Mission> wereLooping = SnapshotOtherLoopingMissions(mission);
                            MissionStore.ClearLoopsConflictingWith(mission,
                                RecordingStore.CommittedTrees, out int _, out int _,
                                "PartnerJourneyInclude");
                            AnnounceClearedLoops(mission, wereLooping);
                        }
                        included = toggled;
                    }
                }

                Color prevColor = GUI.color;
                if (loopAuthoring && !included)
                    GUI.color = DimColor;
                string eventWord = link.ClaimType == BranchPointType.Board ? "Boarded" : "Docked";
                float indent = RecordingsTableUI.SelfConnectorIndent(1);
                if (indent > 0f)
                    GUILayout.Space(indent);
                // The partner's mission: the digest row for this link names it (and holds the
                // Go to target); the link's own merged-stack recording and the tree's original
                // mission stand in when the digest has no row for it.
                MissionPresentation.FindDigestRowForBranchPoint(
                    digest, link.LinkId, out MissionEventRow linkRow);
                string partnerMission = ResolvePartnerMissionName(link.ForeignTreeId, null);
                string goToRecordingId = !string.IsNullOrEmpty(linkRow.GoToRecordingId)
                    ? linkRow.GoToRecordingId
                    : link.MergedChildRecordingId;
                // "Docked partner: <vessel> (mission '<name>')" (T1.7) - the old "Partner journey
                // - X" was designer vocabulary; the row is the vessel that docked with this
                // mission's ship. Drawn through the wrapped-height-correct wide cell (a long
                // partner name wraps).
                DrawWideRowCell(
                    new GUIContent(
                        RecordingsTableUI.TreeConnector(li == total - 1)
                        + MissionPresentation.BuildDockedPartnerLabel(
                            link.ForeignVesselName, partnerMission)
                        // R5 (design-dock-event-graph.md 7.4): the loiter between the
                        // partner's recorded end and the dock is stated, never implied.
                        + FormatLinkLoiterGap(tree, link),
                        MissionPresentation.PartnerJourneyTooltip),
                    1, false);
                GUILayout.Label(KSPUtil.PrintDateCompact(link.DockUT, true),
                    compositionCellLabel, GUILayout.Width(ColW_StartTime));
                DrawEventCell(eventWord, null, compositionCellLabel, ColW_StartEvent);
                GUILayout.Label("", bodyCellLabel, GUILayout.Width(ColW_EndEvent));
                GUILayout.Label("", bodyCellLabel, GUILayout.Width(ColW_EndTime));
                GUI.color = prevColor;
                DrawInteractGoTo(mission, goToRecordingId, partnerMission,
                    link.ForeignVesselName, link.LinkId);
                GUILayout.EndHorizontal();
                rows++;

                if (!included || !loopAuthoring)
                    continue;

                RecordingTree foreignTree = FindTree(trees, link.ForeignTreeId);
                if (foreignTree == null)
                    continue;
                var (foreignStructure, foreignView) = GetMissionView(foreignTree);
                List<MissionCompositionNode> foreignRoots = GetCompositionRoots(foreignTree);
                var journeySet = new HashSet<string>(
                    GetJourneyLegIds(foreignTree, link), System.StringComparer.Ordinal);
                var journeyWindows = MissionCrossTreeDock.ComputeJourneyWindowsByOwner(
                    foreignStructure, foreignView, journeySet);
                // The foreign subtree's label derivations must resolve against the FOREIGN tree's
                // own structure / view (its dock partners and peels live there, not in my tree).
                var foreignCtx = new RowDeriveContext
                {
                    Structure = foreignStructure,
                    View = foreignView,
                    Mission = mission,
                };
                for (int r = 0; r < foreignRoots.Count; r++)
                    rows += DrawMaximalJourneyNodes(foreignRoots[r], mission, journeyWindows, 2,
                        foreignCtx);
                // (journey leg derivation is suppressed + per-frame cached in GetJourneyLegIds;
                // ComputeJourneyWindowsByOwner emits no diagnostics.)
            }

            for (int ri = 0; ri < recorded.Count; ri++)
                rows += DrawRecordedDockPartnerRow(mission, recorded[ri],
                    links.Count + ri == total - 1);
            return rows;
        }

        // One Docked partner row for a dock THIS mission recorded with another mission's vessel
        // (a digest merge row whose partner lives in another tree): the partner and its mission
        // named in the name column, the dock date and verb in their columns, and the Go to.
        // Returns 1.
        private int DrawRecordedDockPartnerRow(Mission mission, MissionEventRow row, bool isLast)
        {
            GUILayout.BeginHorizontal(GUILayout.MinHeight(CompositionRowMinHeight));
            GUILayout.Label("", bodyCellLabel, GUILayout.Width(ColW_Enable));
            // No include toggle: the dock is this tree's own, so there is no foreign journey
            // to pull in. The same single blank cell the roster-atom rows draw.
            GUILayout.Label("", bodyCellLabel, GUILayout.Width(ColW_Index));
            float indent = RecordingsTableUI.SelfConnectorIndent(1);
            if (indent > 0f)
                GUILayout.Space(indent);
            DrawWideRowCell(
                new GUIContent(
                    RecordingsTableUI.TreeConnector(isLast)
                    + MissionPresentation.BuildRecordedDockPartnerLabel(row.PartnerText)),
                1, false);
            GUILayout.Label(KSPUtil.PrintDateCompact(row.UT, true),
                compositionCellLabel, GUILayout.Width(ColW_StartTime));
            DrawEventCell(MissionPresentation.RecordedDockEventWord(row.Verb), null,
                compositionCellLabel, ColW_StartEvent);
            GUILayout.Label("", bodyCellLabel, GUILayout.Width(ColW_EndEvent));
            GUILayout.Label("", bodyCellLabel, GUILayout.Width(ColW_EndTime));
            DrawInteractGoTo(mission, row.GoToRecordingId, null, row.PartnerText,
                row.SourceBranchPointId);
            GUILayout.EndHorizontal();
            return 1;
        }

        /// <summary>
        /// R5 gap statement (design-dock-event-graph.md 7.4): our vessel's OWN recording stops well
        /// before the dock the partner journey begins at, so the journey the player is about to
        /// switch on starts with time nobody recorded. State the gap, never interpolate over it.
        ///
        /// <para>Returns "" (the overwhelmingly common case) when the recording runs right up to the
        /// dock, when it is missing, or when the shortfall is under the threshold. Threshold and
        /// wording are SHARED with the digest's own gap row
        /// (<see cref="MissionEventDigest.GapThresholdSeconds"/> /
        /// <see cref="MissionEventDigest.FormatRowText"/> phrasing) so the two surfaces can never
        /// state the same gap differently. Duration goes through the house formatter
        /// (InvariantCulture).</para>
        /// </summary>
        internal static string FormatLinkLoiterGap(RecordingTree tree, ForeignDockLink link)
        {
            if (tree?.Recordings == null || link == null
                || string.IsNullOrEmpty(link.ClaimedRecordingId))
                return "";
            if (!tree.Recordings.TryGetValue(link.ClaimedRecordingId, out Recording claimed)
                || claimed == null)
                return "";
            double gap = link.DockUT - claimed.EndUT;
            if (double.IsNaN(gap) || gap <= MissionEventDigest.GapThresholdSeconds)
                return "";
            return " (loiter, " + ParsekTimeFormat.FormatDuration(gap) + " - not recorded)";
        }

        // Walks a foreign composition tree and draws every MAXIMAL partner-journey node (a
        // journey node whose ancestors are not journey nodes) - typically the docked-stretch
        // sub-interval and the partner's post-undock offshoot branch. Returns rows drawn.
        private int DrawMaximalJourneyNodes(MissionCompositionNode node, Mission mission,
            IReadOnlyDictionary<string, List<MissionIntervalSelection.RenderWindow>> journeyWindows,
            int depth, RowDeriveContext ctx, MissionCompositionNode parent = null)
        {
            if (node == null)
                return 0;
            if (MissionCrossTreeDock.IsJourneyNode(node, journeyWindows))
                return DrawForeignJourneyNode(node, mission, journeyWindows, depth, true, ctx, parent);
            int rows = 0;
            for (int i = 0; i < node.Children.Count; i++)
                rows += DrawMaximalJourneyNodes(node.Children[i], mission, journeyWindows, depth,
                    ctx, node);
            return rows;
        }

        // Renders one partner-journey interval row (reusing the standard composition row: same
        // include checkbox bound to ExcludedIntervalKeys, greying, collapse) and recurses ONLY
        // into children that are themselves journey nodes or roster atoms - the foreign
        // vessel's own pre-dock / post-departure intervals never render here. Returns rows.
        private int DrawForeignJourneyNode(MissionCompositionNode node, Mission mission,
            IReadOnlyDictionary<string, List<MissionIntervalSelection.RenderWindow>> journeyWindows,
            int depth, bool isLast, RowDeriveContext ctx, MissionCompositionNode parent)
        {
            var drawChildren = new List<MissionCompositionNode>();
            for (int i = 0; i < node.Children.Count; i++)
            {
                MissionCompositionNode c = node.Children[i];
                if (c == null)
                    continue;
                if (!c.IsSelectable || MissionCrossTreeDock.IsJourneyNode(c, journeyWindows))
                    drawChildren.Add(c);
            }

            bool selfExcluded = mission.ExcludedIntervalKeys.Contains(node.HeadLegId);
            bool hasChildren = drawChildren.Count > 0;
            bool collapsed = hasChildren && collapsedLegs.Contains(CollapseKey(mission, node.HeadLegId));
            DrawCompositionRow(node, mission, depth, isLast, true, selfExcluded, selfExcluded,
                hasChildren, collapsed, ctx, parent);
            int rows = 1;

            if (hasChildren && !collapsed)
            {
                for (int i = 0; i < drawChildren.Count; i++)
                {
                    MissionCompositionNode c = drawChildren[i];
                    bool childLast = i == drawChildren.Count - 1;
                    if (c.IsSelectable)
                    {
                        rows += DrawForeignJourneyNode(c, mission, journeyWindows, depth + 1,
                            childLast, ctx, node);
                    }
                    else
                    {
                        // Roster atom: greys with its owning interval, no checkbox. Reuse the
                        // standard recursion (its children, if any, are atoms too).
                        rows += DrawCompositionNode(c, mission, depth + 1, childLast,
                            selfExcluded, ctx, node);
                    }
                }
            }
            return rows;
        }
    }
}
