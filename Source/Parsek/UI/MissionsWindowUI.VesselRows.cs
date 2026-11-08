using System.Collections.Generic;
using Parsek.Logistics;
using UnityEngine;

namespace Parsek
{
    // The expanded vessel rows: the vessel row and composition nodes, the chapter header rows and their cache, and the start / event / blank data cells.
    internal partial class MissionsWindowUI
    {
        // The per-tree read model the composition rows' label derivations need: the delta-phrased
        // interval label (T1.3) resolves the peeled sibling from the parent node's children, and
        // the same-tree dock partner (T1.4) resolves through the structure + through-line view.
        // Carried down the recursion (rather than read from a field) because a partner-journey
        // subtree renders a FOREIGN tree's nodes and must resolve against that tree's own models.
        private struct RowDeriveContext
        {
            public MissionStructure Structure;
            public MissionThroughLineView View;
            // The mission whose window is rendering, for the dock-event-graph naming
            // fallback (design-dock-event-graph.md 6.5): the graph lookup keys on the
            // mission's OWN tree, so on a foreign partner-journey subtree it resolves
            // nothing and the bare event word stands, exactly as before.
            public Mission Mission;
            // The tree's shared vessel names (MissionVesselNaming), for the interval rows'
            // "after undock: X left" peel name; null on a foreign partner-journey subtree.
            public IReadOnlyDictionary<string, string> VesselNames;
        }

        // T2.2: one FLATTENED row per physical vessel / EVA kerbal. Depth encodes separation
        // lineage only (the booster under the ship it left), never time; the event chain renders
        // inline after the name; the vessel's own structural intervals are an expandable flat
        // detail (Advanced only - the detail exists to host the per-interval checkboxes Basic
        // hides), each with its independent include checkbox. The per-vessel checkbox expands to
        // the vessel's OWN explicit interval keys (never a child's), so the non-cascading
        // ExcludedIntervalKeys contract is untouched. Returns the number of rows drawn.
        private int DrawVesselRow(MissionVesselRow row, Mission mission, int depth, bool isLast,
            RowDeriveContext ctx)
        {
            if (row == null || row.Intervals.Count == 0)
                return 0;

            bool loopAuthoring = ShowsLoopAuthoringControls(ParsekUI.AppliedUiComplexityMode);
            MissionVesselInclusion inclusion =
                MissionVesselRowBuilder.ClassifyInclusion(row, mission.ExcludedIntervalKeys);
            // Loop-selection styling is Advanced-only: in Basic an excluded vessel draws plain,
            // since the selection it reports only means something to a loop Basic cannot see.
            bool greyed = loopAuthoring && inclusion == MissionVesselInclusion.None;
            // A single-interval vessel has no detail to expand; Basic never expands (the detail
            // is the authoring surface). Expandability never depends on the inclusion state, so
            // a checkbox click cannot change the control count mid-frame.
            bool expandable = loopAuthoring && row.Intervals.Count > 1;
            string expandKey = CollapseKey(mission, row.OwnerHeadId);
            bool expanded = expandable && expandedVessels.Contains(expandKey);

            GUILayout.BeginHorizontal(GUILayout.MinHeight(CompositionRowMinHeight));

            // Blank enable slot (see DrawCompositionRow): first column totals the recordings
            // tab's [enable+index] width so vessel names line up with the recordings "Name".
            GUILayout.Label("", bodyCellLabel, GUILayout.Width(ColW_Enable));

            // Per-vessel include checkbox (Advanced): writes the vessel's own EXPLICIT interval
            // keys. Click on a fully-included vessel excludes everything; click on a None OR
            // PARTIAL vessel includes everything - the destructive direction is reserved for
            // the unambiguous state, so a hand-authored partial selection is first COMPLETED
            // (recoverable one interval at a time in the expanded detail) rather than dropped
            // wholesale by a click made to see what the box does.
            if (loopAuthoring)
            {
                bool shownChecked = inclusion != MissionVesselInclusion.None;
                bool toggled = GUILayout.Toggle(shownChecked, VesselIncludeCheckboxContent,
                    GUILayout.Width(ColW_Index), GUILayout.ExpandHeight(true));
                if (toggled != shownChecked)
                {
                    bool include = inclusion != MissionVesselInclusion.All;
                    int changed = MissionVesselRowBuilder.ApplyVesselInclusion(
                        row, include, mission.ExcludedIntervalKeys);
                    ParsekLog.Info("Mission",
                        $"Mission '{mission.Name}' vessel '{row.VesselName}' " +
                        $"(head={row.OwnerHeadId}) included={include} " +
                        $"(was {inclusion}) keysChanged={changed}");
                }
            }
            else
            {
                GUILayout.Label("", bodyCellLabel, GUILayout.Width(ColW_Index));
            }

            Color prevColor = GUI.color;
            if (greyed)
                GUI.color = DimColor;

            float indent = RecordingsTableUI.SelfConnectorIndent(depth);
            if (indent > 0f)
                GUILayout.Space(indent);
            string connector = depth > 0 ? RecordingsTableUI.TreeConnector(isLast) : "";
            string caret = expandable ? (expanded ? CaretDown : CaretRight) : "";
            string partial = loopAuthoring && inclusion == MissionVesselInclusion.Partial
                ? " (partial)" : "";
            string phrase = row.EventPhrase ?? "";
            string wide = connector + caret + row.VesselName + partial
                + (phrase.Length > 0 ? "   " + phrase : "");
            // The event chain can outrun the cell, so the full chain rides the tooltip - along
            // with the vessel's final composition ("pod x1, crew x2"), which the flattened row
            // no longer prints inline and which Basic (no interval expansion) could otherwise
            // not see at all. Persons skip it (their label IS their name).
            string tooltip = phrase.Length > 0 ? phrase : null;
            if (!row.IsPerson && row.Intervals.Count > 0)
            {
                string aboard = row.Intervals[row.Intervals.Count - 1].CompositionLabel;
                if (!string.IsNullOrEmpty(aboard))
                    // " - ", never "\n": this tooltip echoes in the Recordings window's
                    // ONE-line help strip, where a newline clips whatever follows it.
                    tooltip = tooltip != null ? tooltip + " - Aboard: " + aboard : "Aboard: " + aboard;
            }
            var wideContent = new GUIContent(wide, tooltip);

            if (DrawWideRowCell(wideContent, depth, expandable))
            {
                if (expanded) expandedVessels.Remove(expandKey);
                else expandedVessels.Add(expandKey);
            }

            // The vessel's whole span + bounding events. The start-event cell reuses the T1.4
            // dock-partner naming (relevant when a vessel's line BEGINS at a dock/board).
            GUILayout.Label(KSPUtil.PrintDateCompact(row.StartUT, true), compositionCellLabel,
                GUILayout.Width(ColW_StartTime));
            DrawStartEventCell(row.Intervals[0], ctx);
            DrawEventCell(row.EndEvent, null, compositionCellLabel, ColW_EndEvent);
            GUILayout.Label(KSPUtil.PrintDateCompact(row.EndUT, true), compositionCellLabel,
                GUILayout.Width(ColW_EndTime));

            GUI.color = prevColor;

            // Interact: the vessel's head recording's Fly / Stash + Seal (the only interval key
            // that IS a real recording id - same resolution the first interval row had in the
            // staircase), or a blank cell.
            // The run the row ends on carries it (MissionVesselRow.InteractHeadId): across an
            // own-side undock that is the ship's own post-undock run, not the launch head.
            if (TryResolveCommittedRecording(row.InteractHeadId ?? row.OwnerHeadId,
                    out int reFlyIdx, out Recording reFlyRec))
                DrawInteractReFly(reFlyRec, reFlyIdx);
            else
                DrawInteractBlank();
            GUILayout.EndHorizontal();
            int rows = 1;

            // The expanded interval detail: this vessel's structural intervals as FLAT, equally
            // indented sub-rows with their independent checkboxes and the four date columns.
            // Interval j's composition-graph parent is interval j-1 (whose children carry the
            // peel that ended it), so the T1.3 delta labels ride along.
            if (expanded)
            {
                for (int j = 0; j < row.Intervals.Count; j++)
                {
                    MissionCompositionNode interval = row.Intervals[j];
                    bool selfExcluded = mission.ExcludedIntervalKeys.Contains(interval.HeadLegId);
                    DrawCompositionRow(interval, mission, depth + 1,
                        j == row.Intervals.Count - 1 && row.Children.Count == 0,
                        true, selfExcluded, selfExcluded, false, false,
                        ctx, j > 0 ? row.Intervals[j - 1] : null,
                        MissionVesselRowBuilder.IntervalDetailLabel(row, j, ctx.VesselNames));
                    rows++;
                }
            }

            // Separation lineage: the pieces that left this vessel, one level deeper.
            for (int c = 0; c < row.Children.Count; c++)
            {
                rows += DrawVesselRow(row.Children[c], mission, depth + 1,
                    c == row.Children.Count - 1, ctx);
            }
            return rows;
        }

        // Renders one composition node (a structural interval, a peeled-off branch, or a roster
        // atom) and recurses into its children. Every selectable node (interval / branch) carries
        // an INDEPENDENT include checkbox bound to Mission.ExcludedIntervalKeys: unchecking it
        // start/end-trims just that segment, with NO cascade (excluding the launch interval keeps
        // the post-decouple survivor checked, which is the whole point). A roster atom carries no
        // checkbox and greys with its owning interval. Since T2.2 the mission path renders
        // through DrawVesselRow above; this recursion remains the renderer for the foreign
        // partner-journey subtrees (DrawForeignJourneyNode) and the interval detail rows.
        private int DrawCompositionNode(MissionCompositionNode node,
            Mission mission, int depth, bool isLast, bool parentExcluded,
            RowDeriveContext ctx, MissionCompositionNode parent)
        {
            if (node == null)
                return 0;

            bool selectable = node.IsSelectable;
            bool selfExcluded = selectable && mission.ExcludedIntervalKeys.Contains(node.HeadLegId);
            // A selectable interval/branch greys only when IT is excluded (independent toggles);
            // a roster atom greys with its owning interval (parentExcluded).
            bool greyed = selectable ? selfExcluded : parentExcluded;
            bool hasChildren = node.Children.Count > 0;
            bool collapsed = hasChildren && collapsedLegs.Contains(CollapseKey(mission, node.HeadLegId));

            DrawCompositionRow(node, mission, depth, isLast, selectable, selfExcluded, greyed,
                hasChildren, collapsed, ctx, parent);
            int rows = 1;

            if (hasChildren && !collapsed)
            {
                // Atoms grey with their interval; sub-intervals are independent (own state).
                bool childParentExcluded = selectable ? selfExcluded : parentExcluded;
                for (int i = 0; i < node.Children.Count; i++)
                {
                    bool childLast = i == node.Children.Count - 1;
                    rows += DrawCompositionNode(node.Children[i], mission,
                        depth + 1, childLast, childParentExcluded, ctx, node);
                }
            }
            return rows;
        }

        // labelOverride: the vessel row's own interval label (MissionVesselRowBuilder.
        // IntervalDetailLabel), which knows the row's own-side undocks; null derives the label
        // from the composition graph (the foreign partner-journey subtrees).
        private void DrawCompositionRow(MissionCompositionNode node, Mission mission,
            int depth, bool isLast, bool selectable, bool selfExcluded, bool greyed,
            bool hasChildren, bool collapsed,
            RowDeriveContext ctx, MissionCompositionNode parent, string labelOverride = null)
        {
            // MinHeight floors the row at the recordings-table row stride so the rows do not pack
            // too tightly and the per-row Fly / Seal button has room (label-only cells alone measure
            // shorter than a recordings row).
            GUILayout.BeginHorizontal(GUILayout.MinHeight(CompositionRowMinHeight));

            // Blank enable slot (no per-row enable in missions) so the first column totals the
            // recordings tab's [enable+index] width and the vessel name lines up with the
            // recordings "Name" column. The include checkbox then sits in the index slot, under "#".
            GUILayout.Label("", bodyCellLabel, GUILayout.Width(ColW_Enable));

            // Include checkbox on every interval / branch (no cascade - unchecking drops just this
            // segment); a blank cell on roster atoms - and on every row in Basic, which hides the
            // loop-authoring controls (ShowsLoopAuthoringControls). The else branch is the SAME
            // single blank cell either way, so the "#" column keeps its width and the rows stay
            // aligned with the header. An exclusion authored in Advanced is untouched: the row
            // still renders greyed, it just cannot be re-picked until the player returns.
            if (selectable && ShowsLoopAuthoringControls(ParsekUI.AppliedUiComplexityMode))
            {
                bool shownChecked = !selfExcluded;
                // ExpandHeight so the checkbox fills the row height and the toggle style centers it
                // vertically (matching the vertically-centered name text), rather than top-aligning.
                // The tooltip is the F2 fix (T1.5): this checkbox is LOOP-UNIT membership, not ghost
                // visibility - the effect no player would guess from an unlabelled tick box.
                bool toggled = GUILayout.Toggle(shownChecked, IntervalIncludeCheckboxContent,
                    GUILayout.Width(ColW_Index), GUILayout.ExpandHeight(true));
                if (toggled != shownChecked)
                    ApplyIntervalInclusion(mission, node.HeadLegId, toggled);
            }
            else
            {
                GUILayout.Label("", bodyCellLabel, GUILayout.Width(ColW_Index));
            }

            Color prevColor = GUI.color;
            if (greyed)
                GUI.color = DimColor;

            float indent = RecordingsTableUI.SelfConnectorIndent(depth);
            if (indent > 0f)
                GUILayout.Space(indent);
            string connector = depth > 0 ? RecordingsTableUI.TreeConnector(isLast) : "";
            string caret = hasChildren ? (collapsed ? CaretRight : CaretDown) : "";
            // "Vessel (composition)" for the FIRST interval of a vessel (and for atoms / EVA rows,
            // whose name already equals the composition); a LATER interval that starts at a
            // separation leads with the delta instead of repeating the vessel name down the
            // staircase ("after undock: Kerbal X Lander left - (pod x1, crew x2)", T1.3). The
            // interval key equals the through-line head only on the vessel's first interval.
            // A roster atom has no OwnerHeadId and no boundary of its own, so it never delta-phrases.
            string label = labelOverride;
            if (label == null)
            {
                bool isFirstInterval = node.IsAtom
                    || string.Equals(node.HeadLegId, node.OwnerHeadId, System.StringComparison.Ordinal);
                string peeledSibling = isFirstInterval
                    ? null
                    : MissionPresentation.ResolvePeeledSiblingVesselName(parent, node, ctx.VesselNames);
                label = MissionPresentation.BuildIntervalRowLabel(
                    node.VesselName, node.CompositionLabel, isFirstInterval,
                    node.StartEvent, peeledSibling);
            }
            string wide = connector + caret + label;

            // Wrapped-height-correct wide cell (see DrawWideRowCell): a long T1.3 delta label
            // wraps, and the naive ExpandWidth label got a rect a line short.
            if (DrawWideRowCell(new GUIContent(wide), depth, hasChildren))
            {
                string key = CollapseKey(mission, node.HeadLegId);
                if (collapsedLegs.Contains(key)) collapsedLegs.Remove(key);
                else collapsedLegs.Add(key);
            }

            // Interval / vessel rows show their span + bounding events; roster atoms inherit the
            // parent's span, so their time columns stay blank.
            if (!node.IsAtom)
            {
                GUILayout.Label(KSPUtil.PrintDateCompact(node.StartUT, true), compositionCellLabel, GUILayout.Width(ColW_StartTime));
                DrawStartEventCell(node, ctx);
                DrawEventCell(node.EndEvent, null, compositionCellLabel, ColW_EndEvent);
                GUILayout.Label(KSPUtil.PrintDateCompact(node.EndUT, true), compositionCellLabel, GUILayout.Width(ColW_EndTime));
            }
            else
            {
                GUILayout.Label("", bodyCellLabel, GUILayout.Width(ColW_StartTime));
                GUILayout.Label("", bodyCellLabel, GUILayout.Width(ColW_StartEvent));
                GUILayout.Label("", bodyCellLabel, GUILayout.Width(ColW_EndEvent));
                GUILayout.Label("", bodyCellLabel, GUILayout.Width(ColW_EndTime));
            }

            // Restore the normal colour before the actionable Re-Fly cell + the trailing Archive
            // cell, so an excluded (greyed) interval's Fly / Seal buttons are not dimmed (the
            // recording's re-fly state is independent of whether the interval is looped).
            GUI.color = prevColor;

            // Interact (per-vessel Fly / Seal for unfinished-flight recordings). Only real
            // (non-atom) rows map to a recording; resolve node.HeadLegId to its committed recording
            // and reuse the recordings tab's Re-Fly cell (it shows Fly / Seal only when the
            // recording is an unfinished flight, otherwise a blank cell). Atoms + unresolved rows
            // get a blank Interact cell so the column stays aligned.
            if (!node.IsAtom
                && TryResolveCommittedRecording(node.HeadLegId, out int reFlyIdx, out Recording reFlyRec))
                DrawInteractReFly(reFlyRec, reFlyIdx);
            else
                DrawInteractBlank();

            GUILayout.EndHorizontal();
        }

        // ---- chapter grouping (design-dock-event-graph.md 7.2) ----
        //
        // Own draw region by the design's section-8 file-ownership seam: the chapter header is an
        // ADDITIONAL row, so nothing in the interval staircase above moves and issue-1's row
        // refactor has no textual overlap with this block.

        // Builds (or reuses) this mission's chapters and arms them for the composition walk.
        private void ArmChapters(Mission mission, RecordingTree tree)
        {
            ChapterCacheEntry entry = GetChapters(mission, tree);
            currentChapters = entry.Chapters;
            currentChapterAnchors = entry.AnchorToIndex;
        }

        private void DisarmChapters()
        {
            currentChapters = null;
            currentChapterAnchors = null;
        }

        // Chapters for a mission, rebuilt only when the dock-event graph INSTANCE changes (a
        // committed-topology move). Chapters derive from the tree alone, and every tree change
        // moves the graph signature, so that reference is the whole staleness key - the same
        // argument (and the same residual: an edit that leaves both per-tree counts equal) as the
        // digest cache above.
        private ChapterCacheEntry GetChapters(Mission mission, RecordingTree tree)
        {
            if (mission == null || string.IsNullOrEmpty(mission.Id) || tree == null)
                return default;

            DockEventGraph graph = GetDockEventGraph();
            if (chapterCache.TryGetValue(mission.Id, out ChapterCacheEntry cached)
                && ReferenceEquals(cached.Graph, graph))
                return cached;

            var (structure, _) = GetMissionView(tree);
            List<MissionCompositionNode> compRoots = GetCompositionRoots(tree);
            List<ChapterRoot> roots = MissionChapters.CollectChapterRoots(
                graph, tree, ResolvePartnerMissionName);

            var chapters = new List<MissionChapterEntry>();
            var anchors = new Dictionary<string, int>(System.StringComparer.Ordinal);
            for (int i = 0; i < roots.Count; i++)
            {
                HashSet<string> keys = MissionChapters.ExpandChapterToIntervalKeys(
                    roots[i], structure, compRoots);
                string anchor = MissionChapters.ResolveChapterAnchorKey(keys, compRoots);
                // A chapter that owns no selectable key has nothing to head and nothing to
                // toggle (a debris-only or fully-collapsed sub-story); drawing an inert header
                // would be a row that does nothing. Two chapters landing on the SAME anchor row
                // is likewise a header we cannot place - only one row can carry one - so the
                // later one is dropped. Both outcomes are logged: a chapter that silently fails
                // to render is exactly the thing that is undiagnosable from a screenshot.
                if (string.IsNullOrEmpty(anchor) || anchors.ContainsKey(anchor))
                {
                    ParsekLog.Verbose("Mission",
                        $"Chapters: no header for '{roots[i].Title}' (root={roots[i].RootRecordingId}) " +
                        $"- {(string.IsNullOrEmpty(anchor) ? "owns no selectable interval key" : "anchor '" + anchor + "' already headed by another chapter")}");
                    continue;
                }
                anchors[anchor] = chapters.Count;
                chapters.Add(new MissionChapterEntry
                {
                    Root = roots[i],
                    IntervalKeys = keys,
                    AnchorKey = anchor,
                });
            }

            var entry = new ChapterCacheEntry
            {
                Graph = graph,
                Chapters = chapters,
                AnchorToIndex = anchors,
            };
            chapterCache[mission.Id] = entry;
            return entry;
        }

        /// <summary>
        /// T2.2 adaptation: draws the chapter group header above the FLATTENED vessel row that
        /// carries a chapter's anchor interval. Scans the row's own intervals because a
        /// SwitchContinuation chapter's first interval folds into the pre-switch through-line
        /// (per-line UT floor), so it is not necessarily the row's first interval. At most one
        /// header per row (a row cannot own two chapter anchors: anchors are distinct interval
        /// keys of distinct sub-stories, and a line re-entering a second chapter is not a shape
        /// CollectChapterRoots can produce). Returns rows drawn (0 or 1).
        /// </summary>
        private int TryDrawChapterHeaderRowForVesselRow(Mission mission, MissionVesselRow row)
        {
            if (row == null || row.Intervals == null)
                return 0;
            for (int i = 0; i < row.Intervals.Count; i++)
            {
                int drawn = TryDrawChapterHeaderRow(mission, row.Intervals[i], 1);
                if (drawn > 0)
                    return drawn;
            }
            return 0;
        }

        /// <summary>
        /// Draws the chapter group header when <paramref name="node"/> is a chapter's first row.
        /// Returns the number of rows drawn (0 or 1).
        /// </summary>
        private int TryDrawChapterHeaderRow(Mission mission, MissionCompositionNode node, int depth)
        {
            // IsSelectable is load-bearing, not a shortcut: a roster ATOM carries its owning
            // interval's HeadLegId verbatim (MissionCompositionBuilder.AddAtoms /
            // AddCrewAtom), so keying the anchor lookup on the id alone would redraw the
            // header above every pod / probe / crew leaf of the chapter's first interval.
            if (currentChapterAnchors == null || currentChapters == null
                || node == null || !node.IsSelectable || string.IsNullOrEmpty(node.HeadLegId)
                || !currentChapterAnchors.TryGetValue(node.HeadLegId, out int idx)
                || idx < 0 || idx >= currentChapters.Count)
                return 0;
            DrawChapterHeaderRow(mission, currentChapters[idx], depth);
            return 1;
        }

        private void DrawChapterHeaderRow(Mission mission, MissionChapterEntry chapter, int depth)
        {
            // Read the tri-state ONCE, before the toggle: the click handler mutates the excluded
            // set mid-frame, and a state re-read afterwards could change which CONTROL this row
            // draws between the Layout and Repaint passes of the same frame (IMGUI's
            // "Getting control N's position in a group with only M controls"). The control set is
            // therefore fixed regardless of state - see the mixed-marker note below.
            ChapterSelectionState state = MissionChapters.ClassifyChapterState(
                chapter.IntervalKeys, mission.ExcludedIntervalKeys);

            GUILayout.BeginHorizontal(GUILayout.MinHeight(CompositionRowMinHeight));
            GUILayout.Label("", bodyCellLabel, GUILayout.Width(ColW_Enable));

            // Tri-state as ONE always-drawn Toggle plus a text marker, rather than swapping in a
            // different control for Mixed: swapping would change the control count mid-frame the
            // moment the click lands (the same hazard every expand caret defers around), and
            // there is no existing mixed-state toggle style in this codebase to borrow. Checked
            // means "some of this chapter is included", so one click drops the whole chapter and
            // the next brings all of it back.
            //
            // Gated with its siblings (finding P21): this toggle writes
            // Mission.ExcludedIntervalKeys, which is the loop-authoring set Basic hides - it was
            // the ONE interval-writing control that escaped ShowsLoopAuthoringControls, so a
            // Basic player still had a click here that authored a set nothing else in the mode
            // could see or undo. The else branch is the SAME single blank cell the per-interval
            // and per-vessel rows draw, so the "#" column keeps its width and the rows stay
            // aligned with the header.
            bool loopAuthoring = ShowsLoopAuthoringControls(ParsekUI.AppliedUiComplexityMode);
            if (loopAuthoring)
            {
                bool shownChecked = state != ChapterSelectionState.AllExcluded;
                bool toggled = GUILayout.Toggle(shownChecked, ChapterIncludeCheckboxContent,
                    GUILayout.Width(ColW_Index), GUILayout.ExpandHeight(true));
                if (toggled != shownChecked)
                {
                    int changed = MissionChapters.ApplyChapterToggle(
                        chapter.IntervalKeys, mission.ExcludedIntervalKeys, toggled);
                    ParsekLog.Info("Mission",
                        $"chapter '{chapter.Root.Title}' {(toggled ? "include" : "exclude")} " +
                        $"keys={changed.ToString(System.Globalization.CultureInfo.InvariantCulture)} " +
                        $"mission='{mission.Name}'");
                }
            }
            else
            {
                GUILayout.Label("", bodyCellLabel, GUILayout.Width(ColW_Index));
            }

            // The dimming and the "[~]" marker report the loop selection, so they go with the
            // toggle in Basic: the chapter title itself stays in both modes.
            Color prevColor = GUI.color;
            if (loopAuthoring && state == ChapterSelectionState.AllExcluded)
                GUI.color = DimColor;
            float indent = RecordingsTableUI.SelfConnectorIndent(depth);
            if (indent > 0f)
                GUILayout.Space(indent);
            // The header is a group label, not a leg: it deliberately carries no tree connector
            // (the row under it owns that) and no span columns - a chapter's span is exactly the
            // union of the rows below, which are already showing it.
            string marker = loopAuthoring && state == ChapterSelectionState.Mixed ? "[~] " : "";
            GUILayout.Label(marker + (chapter.Root.Title ?? ""),
                compositionCellLabel, GUILayout.ExpandWidth(true));
            GUI.color = prevColor;

            DrawBlankDataCells();
            GUILayout.EndHorizontal();
        }

        // The "Start event" cell. A same-tree Dock / Board boundary names the OTHER vessel that
        // joined ("Docked with Munport Station", T1.4) - the rendezvous is the most narratively
        // important moment in a station mission and the row used to present it as an unexplained
        // inventory jump. The partner is derived from the merge leg's two branch parents (the one
        // that is not this vessel's own line); a cross-tree / single-parent dock falls back to the
        // dock event graph, which also names the partner's mission. The cell itself shows only
        // the event word, like the End event cell beside it: a named phrase ("Docked with Depot
        // Station Duna I") does not fit the column and used to clip mid-name, so the full phrase
        // is the cell's tooltip instead.
        private void DrawStartEventCell(MissionCompositionNode node, RowDeriveContext ctx)
        {
            string eventWord = node.StartEvent ?? "";
            if (!MissionPresentation.IsDockEventWord(eventWord))
            {
                DrawEventCell(eventWord, null, compositionCellLabel, ColW_StartEvent);
                return;
            }

            string partner = MissionPresentation.ResolveSameTreeDockPartnerVesselName(
                ctx.Structure, ctx.View, node.OwnerHeadId, node.StartUT);
            if (string.IsNullOrEmpty(partner))
            {
                // Dock-event-graph fallback (design-dock-event-graph.md 6.5): the two-parent
                // walk above cannot name a CROSS-TREE partner or a RECOVERED same-tree
                // single-parent dock (the A->D shape); the graph resolves both, with the
                // partner's mission name.
                string graphText = GetIntervalDockPartnerText(ctx.Mission, node);
                if (!string.IsNullOrEmpty(graphText))
                {
                    DrawEventCell(eventWord, eventWord + " " + graphText, compositionCellLabel,
                        ColW_StartEvent);
                    return;
                }
                // Fallback (a merge neither resolver can name): the bare word, exactly as
                // before. Lazy overload: this runs per row per pass, so the string must not be
                // built while suppressed.
                ParsekLog.VerboseRateLimited("Mission", "missions-dock-partner-unresolved",
                    () => $"Missions row: dock boundary '{eventWord}' at interval '{node.HeadLegId}' " +
                    "named no partner (two-parent walk and dock graph both silent); " +
                    "showing the bare event word", 30.0);
                DrawEventCell(eventWord, null, compositionCellLabel, ColW_StartEvent);
                return;
            }

            DrawEventCell(eventWord,
                MissionPresentation.BuildDockPartnerStartEventText(eventWord, partner),
                compositionCellLabel, ColW_StartEvent);
        }

        // One event cell (Start event / End event) of a Missions-tab row: the event word in the
        // given style at the given width, with MissionPresentation.BuildEventCellTooltip's
        // tooltip - the fuller phrase when one exists, or the shown text when it measures wider
        // than the cell - so no event cell clips its text without the hover carrying it.
        private void DrawEventCell(string shown, string full, GUIStyle style, float width)
        {
            string text = shown ?? "";
            var content = new GUIContent(text);
            bool clips = text.Length > 0 && style.CalcSize(content).x > width;
            string tip = MissionPresentation.BuildEventCellTooltip(text, full, clips);
            if (tip != null)
                content.tooltip = tip;
            GUILayout.Label(content, style, GUILayout.Width(width));
        }

        // The data columns a label-only row leaves blank (a chapter header's title lives in the
        // name column), plus a blank Interact cell.
        private void DrawBlankDataCells()
        {
            GUILayout.Label("", bodyCellLabel, GUILayout.Width(ColW_StartTime));
            GUILayout.Label("", bodyCellLabel, GUILayout.Width(ColW_StartEvent));
            GUILayout.Label("", bodyCellLabel, GUILayout.Width(ColW_EndEvent));
            GUILayout.Label("", bodyCellLabel, GUILayout.Width(ColW_EndTime));
            DrawInteractBlank();
        }
    }
}
