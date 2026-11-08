using System.Collections.Generic;
using System.Globalization;
using System.Text;
using ClickThroughFix;
using Parsek.Logistics;
using UnityEngine;

namespace Parsek
{
    // The window's sections: the route, dormant and candidate section bubbles, the near-miss and dismissed subsections, section headers, separators and collapse toggles, and row expansion.
    internal partial class LogisticsWindowUI
    {
        private void DrawRouteSectionBubble(string title, string emptyText, List<Route> rows,
            RouteSection section, double currentUT)
        {
            string key = section == RouteSection.Active
                ? LogisticsRoutePresentation.ActiveSectionName
                : LogisticsRoutePresentation.PausedSectionName;
            if (!DrawSectionHeader(key, title, rows.Count, LogisticsRoutePresentation.SectionAccent(key)))
            {
                GUILayout.Space(SpacingSmall);
                return;
            }
            GUILayout.BeginVertical(GUI.skin.box);
            // L2: clickable sort headers (cached re-sort). The Active and Paused tables
            // share the sort column / direction; clicking a header re-sorts both.
            DrawRouteSortableHeader();
            if (rows.Count == 0)
                GUILayout.Label("  " + emptyText, parentUI.GetEmptyStateStyle());
            else
            {
                // Draw the cached-sorted rows (re-sorted only on count / sort-state /
                // legibility-stamp change, never per frame). Row index is the display
                // position in the sorted list.
                List<Route> sorted = GetSortedRoutesForSection(rows, section);
                for (int i = 0; i < sorted.Count; i++)
                    DrawRouteRow(sorted[i], section, i + 1, currentUT);
            }
            GUILayout.EndVertical();
            GUILayout.Space(SpacingSmall);
        }

        /// <summary>
        /// Draws the collapsed-by-default "Dormant Routes (N)" disclosure bubble
        /// (rewind-visibility follow-up). Shown ONLY when at least one route is
        /// dormant (<see cref="LogisticsDormantPresentation.ShouldShowDormantSection"/>);
        /// per route: display name, "appears at &lt;date&gt;" (the route's
        /// <see cref="Route.CreatedUT"/> through the house
        /// <c>KSPUtil.PrintDateCompact</c> formatter, guarded off-Unity), and a
        /// Delete button deferred through
        /// <see cref="pendingConfirmDeleteDormantRoute"/> into the same
        /// confirm-dialog flow as committed-route deletion. Read-only otherwise:
        /// a dormant route cannot be activated, edited, or expanded - it does
        /// not exist on the timeline yet. Reads <see cref="RouteStore.DormantRoutes"/>
        /// directly (a plain list read, no derivation, so it is draw-path safe).
        /// </summary>
        private void DrawDormantSectionBubble()
        {
            IReadOnlyList<Route> dormant = RouteStore.DormantRoutes;
            if (!LogisticsDormantPresentation.ShouldShowDormantSection(dormant?.Count ?? 0))
                return;

            GUILayout.BeginVertical(GUI.skin.box);
            bool expanded = expandedRows.Contains(DormantSectionKey);
            if (cachedDormantHeaderCount != dormant.Count
                || cachedDormantHeaderExpanded != expanded
                || cachedDormantHeader == null)
            {
                string arrow = expanded ? "▼" : "▶";
                cachedDormantHeader = new GUIContent(
                    $"{arrow} {LogisticsDormantPresentation.DormantSectionTitle(dormant.Count)}",
                    DormantSectionTooltip);
                cachedDormantHeaderCount = dormant.Count;
                cachedDormantHeaderExpanded = expanded;
            }
            GUILayout.BeginHorizontal();
            GUILayout.Space(8f);
            if (GUILayout.Button(cachedDormantHeader, GUI.skin.label, GUILayout.ExpandWidth(true)))
                ToggleExpanded(DormantSectionKey, "dormant section");
            GUILayout.EndHorizontal();

            if (expanded)
            {
                for (int i = 0; i < dormant.Count; i++)
                {
                    Route route = dormant[i];
                    if (route == null) continue;
                    string display = LogisticsDormantPresentation.DormantRouteDisplayName(route.Name, route.Id);
                    string appears = LogisticsDormantPresentation.DormantAppearsLabel(
                        route.CreatedUT, SafePrintDateCompact(route.CreatedUT));
                    GUILayout.BeginHorizontal();
                    GUILayout.Space(24f);
                    GUILayout.Label($"{display} - {appears}", detailStyle, GUILayout.ExpandWidth(true));
                    // Delete sits in an Interact cell, one Interact single wide, like
                    // every other row action in this window.
                    GUILayout.BeginHorizontal(GUILayout.Width(interactColumnWidth));
                    GUILayout.Space(MissionsWindowUI.InteractCellInset);
                    if (GUILayout.Button(new GUIContent("Delete",
                            "Delete this dormant route. It will never re-materialize."),
                            detailSlotButtonStyle, GUILayout.Width(InteractSingleWidth)))
                        pendingConfirmDeleteDormantRoute = route;
                    GUILayout.FlexibleSpace();
                    GUILayout.EndHorizontal();
                    GUILayout.EndHorizontal();
                }
            }
            GUILayout.EndVertical();
            GUILayout.Space(SpacingSmall);
        }

        /// <summary>
        /// House date formatting (<c>KSPUtil.PrintDateCompact</c>, the same
        /// formatter the Recordings / Missions / Timeline windows use) guarded
        /// for off-Unity contexts: returns null when the formatter is
        /// unavailable so <see cref="LogisticsDormantPresentation.DormantAppearsLabel"/>
        /// renders its honest "&lt;unknown&gt;" fallback.
        /// </summary>
        private static string SafePrintDateCompact(double ut)
        {
            if (ut < 0.0) return null;
            try { return KSPUtil.PrintDateCompact(ut, true); }
            catch { return null; }
        }

        /// <summary>
        /// The Candidates empty-state sentence. With near-misses listed below it the player
        /// already flew such runs, so it points at that list instead of repeating how to fly one.
        /// </summary>
        internal static string CandidatesEmptyStateText(int nearMissCount)
        {
            return nearMissCount > 0
                ? "No supply runs to offer yet. The list below shows why your finished missions cannot become routes; open a reason to see them."
                : "No supply runs to offer yet. Fly a cargo run that docks, transfers cargo and undocks, then finish the mission.";
        }

        private void DrawCandidateSectionBubble(string title, List<RouteCandidate> rows, List<RouteNearMiss> nearMisses)
        {
            if (!DrawSectionHeader(LogisticsRoutePresentation.CandidatesSectionName, title, rows.Count,
                    LogisticsRoutePresentation.SectionAccent(LogisticsRoutePresentation.CandidatesSectionName)))
            {
                GUILayout.Space(SpacingSmall);
                return;
            }
            GUILayout.BeginVertical(GUI.skin.box);
            DrawCandidateColumnHeader();
            if (rows.Count == 0)
                GUILayout.Label("  " + CandidatesEmptyStateText(nearMisses?.Count ?? 0), parentUI.GetEmptyStateStyle());
            for (int i = 0; i < rows.Count; i++)
                DrawCandidateRow(rows[i], i + 1);

            // M3: a collapsible subsection that surfaces WHY a recently committed tree
            // is NOT a candidate (not fully sealed, or sealed-but-ineligible), so the
            // player can tell "I have not committed/sealed it yet" apart from "the run
            // does not match the dock-deliver-undock proof". Reads only the cached list.
            DrawNearMissSubsection(nearMisses);

            // M6 candidate intent helper: the collapsed "Dismissed (N)" subsection
            // listing player-dismissed trees, each with a Restore control. Reads
            // only the cached rows (rebuilt on the ~1 Hz candidate refresh).
            DrawDismissedSubsection();

            GUILayout.EndVertical();
            GUILayout.Space(SpacingSmall);
        }

        /// <summary>
        /// Draws the M3 "Recently committed trees not yet eligible" collapsible
        /// subsection at the bottom of the Candidates bubble. A caret header toggled
        /// through the shared <see cref="expandedRows"/> / <see cref="ToggleExpanded"/>
        /// idiom (fixed key <see cref="NearMissSectionKey"/>); when expanded, one line
        /// per <see cref="RouteNearMiss"/> showing the tree display name and its
        /// blocking reason from the pure
        /// <see cref="LogisticsRejectPresentation.DescribeNearMiss"/>. Nothing renders
        /// when the cached near-miss list is empty. All reads are against the cached
        /// list (built on the ~1 Hz timer in <see cref="GetNearMisses"/>), never live
        /// on the IMGUI draw path.
        /// </summary>
        private void DrawNearMissSubsection(List<RouteNearMiss> nearMisses)
        {
            if (nearMisses == null || nearMisses.Count == 0)
                return;

            bool expanded = expandedRows.Contains(NearMissSectionKey);
            string arrow = expanded ? "\u25bc" : "\u25b6";
            GUILayout.Space(SpacingSmall);
            GUILayout.BeginHorizontal();
            GUILayout.Space(8f);
            if (GUILayout.Button(
                    new GUIContent(
                        $"{arrow} Missions that cannot become routes yet ({nearMisses.Count.ToString(CultureInfo.InvariantCulture)})",
                        "Finished missions that are not supply runs yet, grouped by reason. Open a reason to see its missions."),
                    GUI.skin.label, GUILayout.ExpandWidth(true)))
                ToggleExpanded(NearMissSectionKey, "near-miss subsection");
            GUILayout.EndHorizontal();

            if (!expanded)
                return;

            List<NearMissGroup> groups = cachedNearMissGroups;
            for (int g = 0; groups != null && g < groups.Count; g++)
                DrawNearMissGroup(groups[g]);
        }

        /// <summary>The <c>expandedRows</c> key of one near-miss reason group.</summary>
        internal static string NearMissGroupRowKey(NearMissGroup group)
            => NearMissGroupKeyPrefix + (group?.Key ?? "<none>");

        /// <summary>
        /// One reason: a caret line "No dock was recorded (18): A, B, C ..." whose hover
        /// names every mission (cut with "+N more" to the strip). Opened, the full reason
        /// sentence once (only when every mission shares it and it says more than the short
        /// reason), then one row per mission with its own Dismiss in the candidates' Interact
        /// column, so Dismiss acts on exactly the mission named on its row. Collapsed by default. Every count read here comes from
        /// the cached groups, rebuilt on a Layout pass only.
        /// </summary>
        private void DrawNearMissGroup(NearMissGroup group)
        {
            if (group == null) return;
            string key = NearMissGroupRowKey(group);
            bool expanded = expandedRows.Contains(key);
            string arrow = expanded ? "\u25bc" : "\u25b6";
            GUILayout.BeginHorizontal();
            GUILayout.Space(24f);
            if (GUILayout.Button(new GUIContent(arrow + " " + group.HeaderText, group.Tooltip ?? string.Empty),
                    routeNameStyle, GUILayout.ExpandWidth(true)))
                ToggleExpanded(key, "near-miss group " + group.Key);
            GUILayout.EndHorizontal();

            if (!expanded)
                return;

            if (group.SharedReason != null)
            {
                GUILayout.BeginHorizontal();
                GUILayout.Space(40f);
                GUILayout.Label(group.SharedReason, detailStyle, GUILayout.ExpandWidth(true));
                GUILayout.Space(interactColumnWidth);
                GUILayout.EndHorizontal();
            }
            for (int i = 0; i < group.Members.Count; i++)
            {
                NearMissMember m = group.Members[i];
                GUILayout.BeginHorizontal();
                GUILayout.Space(40f);
                string text = group.MembersShowReason ? m.Label + " - " + m.Reason : m.Label;
                GUILayout.Label(new GUIContent(text, group.MembersShowReason ? m.Tooltip : string.Empty),
                    detailStyle, GUILayout.ExpandWidth(true));
                // The Dismiss cell is drawn on every row (a space where the tree id did not
                // resolve), so the column stays aligned and the count is the cached list's.
                GUILayout.BeginHorizontal(GUILayout.Width(interactColumnWidth));
                GUILayout.Space(MissionsWindowUI.InteractCellInset);
                if (!string.IsNullOrEmpty(m.TreeId))
                {
                    if (GUILayout.Button(new GUIContent(LogisticsRoutePresentation.DismissButtonLabel,
                            LogisticsRoutePresentation.DismissButtonTooltip),
                            detailSlotButtonStyle, GUILayout.Width(InteractSingleWidth)))
                    {
                        pendingDismissTreeId = m.TreeId;
                        pendingDismissLabel = m.Label;
                    }
                }
                else
                {
                    GUILayout.Space(InteractSingleWidth);
                }
                GUILayout.FlexibleSpace();
                GUILayout.EndHorizontal();
                GUILayout.EndHorizontal();
            }
        }

        /// <summary>
        /// Draws the M6 "Dismissed (N)" collapsible subsection at the bottom of
        /// the Candidates bubble: the trees the player dismissed as route
        /// candidates, each with a Restore control. Mirrors the near-miss
        /// subsection idiom exactly (caret header via the shared
        /// <see cref="expandedRows"/> / <see cref="ToggleExpanded"/>, fixed key
        /// <see cref="DismissedSectionKey"/>); nothing renders when nothing is
        /// dismissed. Reads only <see cref="cachedDismissedRows"/> (labels
        /// resolved on the ~1 Hz candidate-cache refresh, never live on the
        /// IMGUI draw path); Restore defers through
        /// <see cref="pendingRestoreTreeId"/> to <see cref="ApplyPendingActions"/>.
        /// </summary>
        private void DrawDismissedSubsection()
        {
            if (cachedDismissedRows.Count == 0)
                return;

            bool expanded = expandedRows.Contains(DismissedSectionKey);
            string arrow = expanded ? "▼" : "▶";
            GUILayout.Space(SpacingSmall);
            GUILayout.BeginHorizontal();
            GUILayout.Space(8f);
            if (GUILayout.Button(
                    new GUIContent(
                        $"{arrow} Hidden missions ({cachedDismissedRows.Count.ToString(CultureInfo.InvariantCulture)})",
                        "Missions you hid from this list. Restore shows one again."),
                    GUI.skin.label, GUILayout.ExpandWidth(true)))
                ToggleExpanded(DismissedSectionKey, "dismissed subsection");
            GUILayout.EndHorizontal();

            if (!expanded)
                return;

            for (int i = 0; i < cachedDismissedRows.Count; i++)
            {
                (string treeId, string label) = cachedDismissedRows[i];
                GUILayout.BeginHorizontal();
                GUILayout.Space(24f);
                GUILayout.Label(label, detailStyle, GUILayout.ExpandWidth(true));
                // Restore sits in an Interact cell, one Interact single wide, like
                // every other row action in this window.
                GUILayout.BeginHorizontal(GUILayout.Width(interactColumnWidth));
                GUILayout.Space(MissionsWindowUI.InteractCellInset);
                if (GUILayout.Button(new GUIContent("Restore",
                        "Offer this mission as a supply run candidate again."),
                        detailSlotButtonStyle, GUILayout.Width(InteractSingleWidth)))
                {
                    pendingRestoreTreeId = treeId;
                    pendingRestoreLabel = label;
                }
                GUILayout.FlexibleSpace();
                GUILayout.EndHorizontal();
                GUILayout.EndHorizontal();
            }
        }

        // Display label for a near-miss tree: its mission's current name, else its TreeName
        // (MissionStore.DisplayNameForTree), falling back to the short tree id and then
        // "<unnamed>" so a row is never blank.
        internal static string NearMissTreeLabel(RecordingTree tree)
        {
            if (tree == null)
                return "<unnamed>";
            string name = MissionStore.DisplayNameForTree(tree);
            if (!string.IsNullOrEmpty(name))
                return name;
            if (!string.IsNullOrEmpty(tree.Id))
                return ShortId(tree.Id);
            return "<unnamed>";
        }

        /// <summary>Whether a section is collapsed, for tests and the census.</summary>
        internal bool IsSectionCollapsedForTesting(string sectionName)
            => sectionName != null && collapsedSections.Contains(sectionName);

        /// <summary>Flips a section the way a title-bar click does (applied at once; the
        /// draw-path click queues the same flip for after the draw).</summary>
        internal void ToggleSectionForTesting(string sectionName)
        {
            ApplySectionToggle(sectionName);
        }

        /// <summary>
        /// Draws a section's title bar and returns whether the section is expanded (whether
        /// its body draws this pass). Reads the collapse state as it stood at the start of
        /// the pass; a click only queues the flip.
        /// </summary>
        private bool DrawSectionHeader(string sectionName, string title, int count,
            ParsekUI.StatusColorKind accent)
        {
            if (sectionTitleButtonStyle == null)
            {
                GUIStyle shared = parentUI.GetSectionHeaderStyle();
                sectionTitleButtonStyle = new GUIStyle(shared)
                {
                    alignment = TextAnchor.MiddleCenter,
                    fontSize = (shared.fontSize > 0 ? shared.fontSize : GUI.skin.label.fontSize > 0 ? GUI.skin.label.fontSize : 12)
                        + LogisticsRoutePresentation.SectionTitleFontStep,
                };
            }
            bool expanded = !collapsedSections.Contains(sectionName);
            GUILayout.Space(SpacingSmall);
            // The box-on-box shade the column-header row has (a bare box-label on the window
            // background reads lighter), with the accent bar along its bottom edge.
            GUILayout.BeginVertical(GUI.skin.box);
            if (GUILayout.Button(new GUIContent(
                    LogisticsRoutePresentation.FormatSectionTitle(title, count, expanded),
                    expanded ? "Collapse this section" : "Expand this section"),
                    sectionTitleButtonStyle, GUILayout.ExpandWidth(true)))
                pendingSectionToggle = sectionName;
            GUILayout.Box(GUIContent.none, SectionAccentStyle(accent),
                GUILayout.ExpandWidth(true), GUILayout.Height(LogisticsRoutePresentation.SectionAccentHeight));
            GUILayout.EndVertical();
            return expanded;
        }

        private GUIStyle SectionAccentStyle(ParsekUI.StatusColorKind kind)
        {
            if (sectionAccentStyles.TryGetValue(kind, out GUIStyle style) && style != null
                && style.normal.background != null)
                return style;
            style = ParsekUI.CreateRuleLineStyle(parentUI.GetStatusColor(kind));
            style.margin = new RectOffset(0, 0, 0, 0);
            sectionAccentStyles[kind] = style;
            return style;
        }

        /// <summary>
        /// The rule between two sections, in the middle of a gap twice the old one: the same
        /// 2 px grey rule the Timeline draws for "now" (<see cref="ParsekUI.CreateRuleLineStyle"/>).
        /// </summary>
        private void DrawSectionSeparator()
        {
            if (sectionRuleStyle == null || sectionRuleStyle.normal.background == null)
            {
                sectionRuleStyle = ParsekUI.CreateRuleLineStyle(ParsekUI.RuleLineColor);
                sectionRuleStyle.margin = new RectOffset(0, 0, 0, 0);
            }
            GUILayout.Space(SpacingSmall);
            GUILayout.Box(GUIContent.none, sectionRuleStyle, GUILayout.ExpandWidth(true), GUILayout.Height(2f));
            GUILayout.Space(SpacingSmall);
        }

        /// <summary>
        /// Flips a section. Collapsing one drops an open interval or rename edit on a route
        /// inside it, exactly as the switch to Basic drops an Every edit (discarded like
        /// Escape, never committed): its field is about to stop drawing, so its click-away
        /// commit would land on a control that is gone.
        /// </summary>
        private void ApplySectionToggle(string sectionName)
        {
            if (string.IsNullOrEmpty(sectionName))
                return;
            bool collapse = !collapsedSections.Contains(sectionName);
            if (collapse)
            {
                collapsedSections.Add(sectionName);
                if (intervalEditRouteId != null && RouteIsInSection(intervalEditRouteId, sectionName))
                    ClearIntervalEdit();
                if (renamingRouteId != null && RouteIsInSection(renamingRouteId, sectionName))
                    ClearRouteRename();
            }
            else
            {
                collapsedSections.Remove(sectionName);
            }
            ParsekLog.Verbose("UI",
                $"Logistics section {(collapse ? "collapsed" : "expanded")}: '{sectionName}'");
        }

        private static bool RouteIsInSection(string routeId, string sectionName)
        {
            if (!RouteStore.TryGetRoute(routeId, out Route route) || route == null)
                return false;
            bool paused = LogisticsRoutePresentation.BelongsInPausedTable(route.Status);
            return sectionName == (paused
                ? LogisticsRoutePresentation.PausedSectionName
                : LogisticsRoutePresentation.ActiveSectionName);
        }


        private void ToggleExpanded(string key, string nameForLog)
        {
            if (expandedRows.Contains(key))
            {
                expandedRows.Remove(key);
                ParsekLog.Verbose("UI", $"Logistics row collapsed: '{nameForLog}'");
            }
            else
            {
                expandedRows.Add(key);
                ParsekLog.Verbose("UI", $"Logistics row expanded: '{nameForLog}'");
            }
        }
    }
}
