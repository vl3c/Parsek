using System;
using System.Collections.Generic;
using UnityEngine;

namespace Parsek
{
    /// <summary>
    /// The Mission Profile window ("Parsek - Profile: &lt;mission&gt;",
    /// docs/dev/design-mission-profile.md sections 2.2 to 2.5 and 6): what one mission's
    /// flight did as a tree of phases, burns and events, opened from a mission's Profile
    /// button on the Missions tab. Rows come from the pure
    /// <see cref="MissionProfileBuilder.BuildProfile"/>; this window fills the request from
    /// live data the way the Mission Log does, draws the rows the builder formatted, and
    /// keeps the collapse state. Read-only.
    ///
    /// <para>The frame (rect, resize, scaled draw, input lock) is the Log's
    /// (<see cref="LogTableWindowFrame"/>), and the body follows the Log's table: boxed
    /// header row, forced-scrollbar scroll view, the single-line hover strip, Close. One
    /// instance: reopening it from another mission retargets it. Not in the Basic close set,
    /// like the Log: it is reachable in both modes.</para>
    /// </summary>
    internal class MissionProfileWindowUI : IMissionProfileWindow
    {
        private readonly ParsekUI parentUI;
        private readonly LogTableWindowFrame frame;
        private bool isOpen;
        private Vector2 scrollPos;

        /// <summary>The window-id key, named once for the window call and the seam's
        /// window handle (<c>UiAction op=find</c> scopes a captured tree by it).</summary>
        internal const string WindowIdKey = "ParsekMissionProfile";
        private const string InputLockId = "Parsek_MissionProfileWindow";
        private const string LogName = "Profile window";

        internal const float DefaultWindowWidth = 960f;
        internal const float DefaultWindowHeight = 640f;
        internal const float MinWindowWidth = 480f;
        internal const float MinWindowHeight = 160f;

        /// <summary>How far the first-open rect sits from the Mission Log's, down and right,
        /// so both can be open without covering each other.</summary>
        internal const float CascadeOffset = 80f;

        // Column widths (design 2.3). Element expands.
        internal const float ColW_Time = 100f;
        internal const float ColW_Location = 150f;
        internal const float ColW_Duration = 80f;
        internal const float ColW_Spent = 74f;
        internal const float ColW_Left = 74f;
        internal const float ColW_Result = 190f;
        // One tree level of indent in the Element cell, and the caret's slot.
        internal const float IndentPerDepth = 14f;
        internal const float CaretSlotWidth = 18f;

        private static readonly string CaretDown = new string(new[] { (char)0x25BC });
        private static readonly string CaretRight = new string(new[] { (char)0x25B6 });

        internal const string CaretExpandedTooltip = "Collapse this element.";
        internal const string CaretCollapsedTooltip = "Expand this element.";

        // Current target and the built profile (rebuilt on open and when the change
        // signature moves, never per frame).
        private string treeId;
        private string missionId;
        private string title = "";
        private int changeSignature;
        private MissionProfile profile;
        private List<MissionProfileRow> rows = new List<MissionProfileRow>();
        // Indices into `rows` of the rows no collapsed ancestor hides, re-derived when the
        // rows or the collapse set change.
        private List<int> visibleRows = new List<int>();
        // Collapse state for the session, keyed by mission id + element key (design 2.5).
        private readonly HashSet<string> collapsedKeys = new HashSet<string>(StringComparer.Ordinal);

        private readonly TooltipEchoBox tooltipEcho =
            new TooltipEchoBox(TooltipEchoBox.DefaultSpacing, TooltipEchoBox.SingleLine);

        private GUIStyle bodyCellLabel;
        private GUIStyle bodyCellLabelSource;
        private GUIStyle emptyBodyCellLabel;
        private GUIStyle strongCellLabel;
        private GUIStyle spentCellLabel;
        private GUIStyle caretStyle;

        internal MissionProfileWindowUI(ParsekUI parentUI)
        {
            this.parentUI = parentUI;
            frame = new LogTableWindowFrame(WindowIdKey, InputLockId, LogName, "MissionProfile",
                MinWindowWidth, MinWindowHeight);
        }

        public bool IsOpen
        {
            get { return isOpen; }
            set { isOpen = value; }
        }

        /// <summary>The live window rect (the seam's <c>op=rect</c> and an in-game test).</summary>
        internal Rect WindowRectForTesting
        {
            get { return frame.WindowRect; }
            set { frame.WindowRect = value; }
        }

        /// <summary>How many rows the last open or rebuild produced, Summary included (the
        /// seam's <c>op=target</c> reports it as <c>steps=</c>).</summary>
        internal int StepCountForTesting => rows != null ? rows.Count : 0;

        /// <summary>The target kind for the same report: Mission once opened on one.</summary>
        internal string TargetModeForTesting => profile != null ? "Mission" : "None";

        /// <summary>The window title: <c>"Parsek - Profile: Kerbal X"</c>, the bare prefix
        /// with no name. Pure; the Log's title builder.</summary>
        internal static string BuildWindowTitle(string missionName)
            => StructureListWindowUI.BuildWindowTitle(ProfileLabels.WindowTitlePrefix, missionName);

        /// <summary>First-open rect: the Mission Log's first-open position moved
        /// <see cref="CascadeOffset"/> down and right, at the Profile's own size. Pure.</summary>
        internal static Rect DefaultWindowRect(Rect mainWindowRect)
        {
            Rect log = StructureListWindowUI.DefaultWindowRect(mainWindowRect, false);
            return new Rect(log.x + CascadeOffset, log.y + CascadeOffset,
                DefaultWindowWidth, DefaultWindowHeight);
        }

        // ------------------------------------------------------------------ open / rebuild

        /// <summary>
        /// Opens the Profile on one Mission (the Missions tab's Profile button and the seam's
        /// <c>op=target window=profile</c>). Retargets an open window; everything starts
        /// expanded (design 2.5).
        /// </summary>
        public void OpenForMission(string targetTreeId, string targetMissionId, string displayTitle)
        {
            treeId = targetTreeId;
            missionId = targetMissionId;
            title = displayTitle ?? "";
            ClearCollapsedFor(missionId);
            Rebuild();
            isOpen = true;
            ParsekLog.Info("UI",
                $"Profile window opened: tree={treeId ?? "<null>"} mission={missionId ?? "<none>"} " +
                $"lines={(profile != null ? profile.LineCount : 0)} rows={rows.Count} " +
                $"title='{BuildWindowTitle(title)}'");
        }

        private void Rebuild()
        {
            Mission mission = FindMission(missionId);
            changeSignature = CurrentSignature(mission);
            RecordingTree tree = FindTree(treeId);
            MissionProfile built = MissionProfileBuilder.BuildProfile(BuildRequest(mission, tree));
            SetProfile(built);
        }

        private void SetProfile(MissionProfile built)
        {
            profile = built;
            rows = built != null ? built.Rows : new List<MissionProfileRow>();
            RecomputeVisibleRows();
        }

        private static int CurrentSignature(Mission mission)
        {
            ParsekScenario scenario = ParsekScenario.Instance;
            int supersedeVersion = !ReferenceEquals(scenario, null) ? scenario.SupersedeStateVersion : 0;
            return ComputeChangeSignature(RecordingStore.StateVersion, supersedeVersion,
                mission != null, mission?.Name, mission?.ExcludedIntervalKeys, DisplayNames.Version);
        }

        /// <summary>
        /// The rebuild signature (design 6): the Log's signature (committed recordings, the
        /// mission's presence, name and include set, display names) plus the supersede
        /// version the effective id set reads. Pure; an in-memory compare value.
        /// </summary>
        internal static int ComputeChangeSignature(int stateVersion, int supersedeVersion,
            bool missionFound, string missionName, ICollection<string> excludedIntervalKeys,
            int displayNamesVersion)
        {
            unchecked
            {
                int h = StructureListWindowUI.ComputeChangeSignature(stateVersion, missionFound,
                    missionName, excludedIntervalKeys, displayNamesVersion);
                return h * 31 + supersedeVersion;
            }
        }

        /// <summary>
        /// Re-reads the signature on Layout events only and rebuilds when it moved; closes the
        /// window when its tree is gone (the Route History's deleted-route rule). Inert for a
        /// gallery mock (no target).
        /// </summary>
        private void RefreshIfChanged()
        {
            if (string.IsNullOrEmpty(treeId))
                return;
            if (ShouldCloseForDeletedTree(treeId, FindTree(treeId) != null))
            {
                ParsekLog.Info("UI",
                    $"Profile window closed: tree={treeId} no longer exists (deleted) " +
                    $"title='{BuildWindowTitle(title)}'");
                Close();
                return;
            }
            Mission mission = FindMission(missionId);
            if (CurrentSignature(mission) == changeSignature)
                return;
            int before = rows.Count;
            if (mission != null && !string.IsNullOrEmpty(mission.Name))
                title = mission.Name;
            Rebuild();
            ParsekLog.Info("UI",
                $"Profile window rebuilt on change: tree={treeId} mission={missionId ?? "<none>"} " +
                $"found={(mission != null ? "yes" : "no")} rows={before}->{rows.Count} " +
                $"title='{BuildWindowTitle(title)}'");
        }

        /// <summary>A Profile whose tree is gone closes rather than titling a mission that
        /// no longer exists. Pure.</summary>
        internal static bool ShouldCloseForDeletedTree(string targetTreeId, bool treeFound)
            => !string.IsNullOrEmpty(targetTreeId) && !treeFound;

        // ------------------------------------------------------------------ the request

        // Fills the builder's request the way the Mission Log fills its own build: the vessel
        // names and partner legs from MissionVesselNaming, the part titles and dock partners
        // through the Log's resolvers, plus the Profile's own inputs (effective ids, supersede
        // rows, live body facts, career totals).
        private static MissionProfileRequest BuildRequest(Mission mission, RecordingTree tree)
        {
            var request = new MissionProfileRequest
            {
                Mission = mission,
                Tree = tree,
                BodyFacts = RecordingOptimizer.ResolveBodyFacts,
                DateFormatter = FormatDate,
                PartTitleResolver = StructureListWindowUI.ResolvePartTitle,
                CommittedTrees = RecordingStore.CommittedTrees,
                TreeMissionName = otherTreeId => MissionsWindowUI.ResolvePartnerMissionName(otherTreeId, null),
            };
            if (tree == null)
                return request;

            ParsekScenario scenario = ParsekScenario.Instance;
            List<RecordingSupersedeRelation> supersedes =
                !ReferenceEquals(scenario, null) ? scenario.RecordingSupersedes : null;
            MissionStructure structure = MissionStructureBuilder.Build(tree);
            var partnerLegIds = new HashSet<string>(StringComparer.Ordinal);
            request.Structure = structure;
            request.VesselNames = MissionVesselNaming.Build(tree, structure,
                MissionVesselNaming.LaunchIndex.Build(RecordingStore.CommittedTrees),
                request.TreeMissionName, out MissionVesselNaming.Tally _, null, partnerLegIds);
            request.PartnerLegIds = partnerLegIds;
            request.SupersedeRelations = supersedes;
            request.EffectiveRecordingIds = EffectiveRecordingIdsOf(tree, supersedes);
            string id = tree.Id;
            request.MergePartnerResolver = (bp, viewerId) =>
                StructureListWindowUI.ResolveMergePartner(id, bp, viewerId);
            request.Career = SumCareer(EffectiveState.ComputeELS(), request.EffectiveRecordingIds);
            return request;
        }

        /// <summary>
        /// The tree's effective recording ids for the Profile (design 5): committed and not
        /// superseded, with the active Re-Fly's session suppression IGNORED, so the Profile
        /// follows the Log during a session and shows the flown-again path after the merge.
        /// Pure over the tree and the supersede rows.
        /// </summary>
        internal static HashSet<string> EffectiveRecordingIdsOf(RecordingTree tree,
            IReadOnlyList<RecordingSupersedeRelation> supersedes)
        {
            var ids = new HashSet<string>(StringComparer.Ordinal);
            if (tree?.Recordings == null)
                return ids;
            foreach (KeyValuePair<string, Recording> kv in tree.Recordings)
            {
                if (EffectiveState.IsVisible(kv.Value, supersedes))
                    ids.Add(kv.Value.RecordingId);
            }
            return ids;
        }

        /// <summary>
        /// The career results the mission's ledger rows carry (the Summary's career fact):
        /// science, funds and reputation of every effective ledger action tagged to one of
        /// <paramref name="recordingIds"/>. Known only when at least one row matched. Pure.
        /// </summary>
        internal static MissionProfileCareerTotals SumCareer(IReadOnlyList<GameAction> actions,
            ICollection<string> recordingIds)
        {
            var totals = new MissionProfileCareerTotals();
            if (actions == null || recordingIds == null || recordingIds.Count == 0)
                return totals;
            for (int i = 0; i < actions.Count; i++)
            {
                GameAction a = actions[i];
                if (a == null || string.IsNullOrEmpty(a.RecordingId) || !recordingIds.Contains(a.RecordingId))
                    continue;
                if (AddCareerAction(ref totals, a))
                    totals.Known = true;
            }
            return totals;
        }

        private static bool AddCareerAction(ref MissionProfileCareerTotals t, GameAction a)
        {
            switch (a.Type)
            {
                case GameActionType.ScienceEarning: t.Science += a.ScienceAwarded; return true;
                case GameActionType.FundsEarning: t.Funds += a.FundsAwarded; return true;
                case GameActionType.FundsSpending: t.Funds -= a.FundsSpent; return true;
                case GameActionType.ReputationEarning: t.Reputation += a.NominalRep; return true;
                case GameActionType.ReputationPenalty: t.Reputation -= a.NominalPenalty; return true;
                case GameActionType.MilestoneAchievement:
                    t.Funds += a.CreditedMilestoneFunds;
                    t.Reputation += a.CreditedMilestoneRep;
                    t.Science += a.CreditedMilestoneScience;
                    return true;
                case GameActionType.ContractComplete:
                    t.Funds += a.FundsReward;
                    t.Reputation += a.RepReward;
                    t.Science += a.ScienceReward;
                    return true;
                case GameActionType.ContractFail:
                    t.Funds -= a.FundsPenalty;
                    t.Reputation -= a.RepPenalty;
                    return true;
                default:
                    return false;
            }
        }

        // ------------------------------------------------------------------ collapse

        /// <summary>The collapse-set key of one row of one mission. Pure.</summary>
        internal static string CollapseKey(string forMissionId, string rowKey)
            => (forMissionId ?? "") + "\n" + (rowKey ?? "");

        private void ClearCollapsedFor(string forMissionId)
        {
            string prefix = CollapseKey(forMissionId, "");
            collapsedKeys.RemoveWhere(k => k.StartsWith(prefix, StringComparison.Ordinal));
        }

        /// <summary>Flips one row's caret, as its click does. Returns the new collapsed state.</summary>
        internal bool ToggleCollapsed(string rowKey)
        {
            string key = CollapseKey(missionId, rowKey);
            bool nowCollapsed = !collapsedKeys.Remove(key);
            if (nowCollapsed) collapsedKeys.Add(key);
            RecomputeVisibleRows();
            ParsekLog.Verbose("UI",
                $"Profile window caret: mission={missionId ?? "<none>"} key={rowKey} " +
                $"collapsed={nowCollapsed} visibleRows={visibleRows.Count}/{rows.Count}");
            return nowCollapsed;
        }

        private void RecomputeVisibleRows()
        {
            string forMission = missionId;
            visibleRows = VisibleRowIndices(rows, key => collapsedKeys.Contains(CollapseKey(forMission, key)));
        }

        /// <summary>
        /// The rows no collapsed ancestor hides, as indices into <paramref name="all"/>. A row
        /// is hidden when any row up its ParentKey chain is collapsed; a collapsed row itself
        /// stays drawn (its caret is how it opens again). Pure.
        /// </summary>
        internal static List<int> VisibleRowIndices(IList<MissionProfileRow> all, Func<string, bool> isCollapsed)
        {
            var visible = new List<int>();
            if (all == null)
                return visible;
            var parentOf = new Dictionary<string, string>(StringComparer.Ordinal);
            foreach (MissionProfileRow r in all)
                if (r != null && !string.IsNullOrEmpty(r.Key) && !parentOf.ContainsKey(r.Key))
                    parentOf[r.Key] = r.ParentKey;
            for (int i = 0; i < all.Count; i++)
            {
                if (all[i] != null && !HiddenByAncestor(all[i].ParentKey, parentOf, isCollapsed))
                    visible.Add(i);
            }
            return visible;
        }

        private static bool HiddenByAncestor(string parentKey, Dictionary<string, string> parentOf,
            Func<string, bool> isCollapsed)
        {
            // Bounded by the row count, so a malformed cycle cannot spin. A parent key that
            // names no row hides nothing: there would be no caret to open it again.
            for (int guard = 0; !string.IsNullOrEmpty(parentKey) && guard <= parentOf.Count; guard++)
            {
                if (!parentOf.TryGetValue(parentKey, out string grandParent))
                    return false;
                if (isCollapsed != null && isCollapsed(parentKey))
                    return true;
                parentKey = grandParent;
            }
            return false;
        }

        // ------------------------------------------------------------------ gallery seam

        /// <summary>Everything the target state consists of, so a mock's restore is a
        /// complete inverse.</summary>
        internal struct GalleryTargetSnapshot
        {
            internal string TreeId;
            internal string MissionId;
            internal string Title;
            internal int ChangeSignature;
            internal MissionProfile Profile;
            internal bool IsOpen;
        }

        internal GalleryTargetSnapshot CaptureGalleryTarget()
            => new GalleryTargetSnapshot
            {
                TreeId = treeId,
                MissionId = missionId,
                Title = title,
                ChangeSignature = changeSignature,
                Profile = profile,
                IsOpen = isOpen,
            };

        internal void RestoreGalleryTarget(GalleryTargetSnapshot snapshot)
        {
            treeId = snapshot.TreeId;
            missionId = snapshot.MissionId;
            title = snapshot.Title ?? "";
            changeSignature = snapshot.ChangeSignature;
            SetProfile(snapshot.Profile);
            isOpen = snapshot.IsOpen;
        }

        /// <summary>
        /// Opens the window on a supplied, already built profile, bypassing the store. The
        /// tree id is null on purpose, so <see cref="RefreshIfChanged"/> leaves it alone and
        /// no fabricated id can be resolved later. Reached only from the automation-only
        /// <c>UiAction op=mock</c> applier.
        /// </summary>
        internal void OpenWithGalleryProfile(string displayTitle, MissionProfile mocked)
        {
            treeId = null;
            missionId = mocked?.MissionId;
            title = displayTitle ?? "";
            ClearCollapsedFor(missionId);
            SetProfile(mocked);
            isOpen = true;
        }

        // ------------------------------------------------------------------ draw

        public void DrawIfOpen(Rect mainWindowRect)
        {
            if (!isOpen)
            {
                ReleaseInputLock();
                return;
            }
            if (Event.current != null && Event.current.type == EventType.Layout)
                RefreshIfChanged();
            if (!isOpen)
                return;
            frame.Draw(parentUI, DefaultWindowRect(mainWindowRect), BuildWindowTitle(title), DrawWindow);
        }

        public void ReleaseInputLock()
        {
            frame.ReleaseInputLock();
        }

        private void EnsureStyles()
        {
            GUIStyle sharedCell = parentUI.GetTableCellStyle();
            if (bodyCellLabel != null && ReferenceEquals(bodyCellLabelSource, sharedCell))
                return;
            bodyCellLabel = new GUIStyle(sharedCell)
            {
                padding = new RectOffset(sharedCell.padding.left, sharedCell.padding.right, 0, 0),
                wordWrap = false,
                clipping = TextClipping.Clip,
            };
            bodyCellLabelSource = sharedCell;
            emptyBodyCellLabel = new GUIStyle(bodyCellLabel) { normal = { textColor = ParsekUI.MutedTextColor } };
            strongCellLabel = new GUIStyle(bodyCellLabel) { fontStyle = FontStyle.Bold };
            spentCellLabel = new GUIStyle(bodyCellLabel)
            {
                normal = { textColor = ParsekUI.StatusColor(ParsekUI.StatusColorKind.Orange) }
            };
            // The caret is drawn in the body cell's own style (font, size, vertical padding and
            // margins), so its glyph shares the text's line box and baseline, and every row is one
            // height: a horizontal group takes the smallest vertical margin among its cells, so a
            // caret with other margins makes caret rows a different height from leaf rows.
            caretStyle = new GUIStyle(bodyCellLabel)
            {
                padding = new RectOffset(0, 0, bodyCellLabel.padding.top, bodyCellLabel.padding.bottom),
                margin = new RectOffset(0, 0, bodyCellLabel.margin.top, bodyCellLabel.margin.bottom),
                clipping = TextClipping.Overflow,
            };
        }

        private void DrawWindow(int windowID)
        {
            EnsureStyles();
            GUILayout.Space(ParsekUI.WindowContentTopGapPx);
            DrawProfileTable();
            // Drawn after the rows so the live GUI.tooltip read sees a hovered cell, and
            // directly above Close (the house ordering).
            tooltipEcho.Draw();
            if (GUILayout.Button("Close"))
                Close();
            frame.DrawResizeHandleAndDrag();
        }

        /// <summary>
        /// The table: ONE dark body box holding the pinned column-header row and the scroll
        /// view of rows. The vertical scrollbar is forced so the gutter the header reserves
        /// is always taken (the Log's contract, ParsekUI.TableRowHorizontalInsetPx).
        /// </summary>
        private void DrawProfileTable()
        {
            GUILayout.BeginVertical(parentUI.GetTableBodyBoxStyle(), GUILayout.ExpandHeight(true));
            DrawColumnHeader();
            scrollPos = GUILayout.BeginScrollView(scrollPos, false, true,
                GUI.skin.horizontalScrollbar, GUI.skin.verticalScrollbar,
                parentUI.GetTableScrollViewStyle(), GUILayout.ExpandHeight(true));
            if (rows.Count == 0)
                DrawEmptyBodyRow();
            else
                DrawProfileRows();
            GUILayout.EndScrollView();
            GUILayout.EndVertical();
        }

        private void DrawColumnHeader()
        {
            GUIStyle header = parentUI.GetColumnHeaderStyle();
            GUILayout.BeginHorizontal(parentUI.GetTableHeaderRowStyle());
            GUILayout.Label("Time", header, GUILayout.Width(ColW_Time));
            GUILayout.Label("Element", header, GUILayout.ExpandWidth(true));
            GUILayout.Label("Location", header, GUILayout.Width(ColW_Location));
            GUILayout.Label("Duration", header, GUILayout.Width(ColW_Duration));
            GUILayout.Label("Spent", header, GUILayout.Width(ColW_Spent));
            GUILayout.Label("Left", header, GUILayout.Width(ColW_Left));
            GUILayout.Label("Orbit / result", header, GUILayout.Width(ColW_Result));
            GUILayout.EndHorizontal();
        }

        // The empty table's one body row (design 2.2): the builder's empty sentence in the
        // house muted grey; before any target, the Log's bare-open sentence.
        private void DrawEmptyBodyRow()
        {
            GUILayout.BeginHorizontal(parentUI.GetTableRowStyle());
            GUILayout.Label(StructureListWindowUI.EmptyNoTargetText, emptyBodyCellLabel);
            GUILayout.EndHorizontal();
        }

        private void DrawProfileRows()
        {
            GUIStyle cellStyle = bodyCellLabel;
            for (int v = 0; v < visibleRows.Count; v++)
            {
                MissionProfileRow row = rows[visibleRows[v]];
                // A clipped cell (a long Summary fact, a long label) still reads in full in the
                // hover strip: a row with no hover of its own shows its element text there.
                string hover = !string.IsNullOrEmpty(row.Hover) ? row.Hover : (row.Element ?? "");
                // Same shared row container as the header row above (one inset).
                GUILayout.BeginHorizontal(parentUI.GetTableRowStyle());
                GUILayout.Label(new GUIContent(row.Time ?? "", hover), cellStyle, GUILayout.Width(ColW_Time));
                // The Element cell expands like the Log's Event cell: the tree indent, the
                // caret (phases and line headers) or its empty slot, then the label.
                // MinWidth(0) on the cell and its label: a non-wrapping label otherwise demands its
                // full text width, which widens the scroll view's content past the header row and
                // pushes every column right of the Element heading out of line.
                GUILayout.BeginHorizontal(GUILayout.MinWidth(0f), GUILayout.ExpandWidth(true));
                GUILayout.Space(Mathf.Max(0, row.Depth) * IndentPerDepth);
                DrawCaretSlot(row);
                GUILayout.Label(new GUIContent(row.Element ?? "", hover), ElementStyleFor(row, cellStyle),
                    GUILayout.MinWidth(0f));
                GUILayout.EndHorizontal();
                GUILayout.Label(new GUIContent(row.Location ?? "", hover), cellStyle, GUILayout.Width(ColW_Location));
                GUILayout.Label(new GUIContent(row.Duration ?? "", hover), cellStyle, GUILayout.Width(ColW_Duration));
                GUILayout.Label(new GUIContent(row.Spent ?? "", hover),
                    DrawsSpentInOrange(row) ? spentCellLabel : cellStyle, GUILayout.Width(ColW_Spent));
                GUILayout.Label(new GUIContent(row.Left ?? "", hover), cellStyle, GUILayout.Width(ColW_Left));
                GUILayout.Label(new GUIContent(row.Result ?? "", hover), cellStyle, GUILayout.Width(ColW_Result));
                GUILayout.EndHorizontal();
            }
        }

        private void DrawCaretSlot(MissionProfileRow row)
        {
            if (!row.HasCaret)
            {
                GUILayout.Space(CaretSlotWidth);
                return;
            }
            bool collapsed = collapsedKeys.Contains(CollapseKey(missionId, row.Key));
            var caret = new GUIContent(collapsed ? CaretRight : CaretDown,
                collapsed ? CaretCollapsedTooltip : CaretExpandedTooltip);
            if (GUILayout.Button(caret, caretStyle, GUILayout.Width(CaretSlotWidth)))
                ToggleCollapsed(row.Key);
        }

        // The Empty row reads in the house muted grey, the Summary and line headers bold,
        // every other element in the body cell style.
        private GUIStyle ElementStyleFor(MissionProfileRow row, GUIStyle cellStyle)
        {
            if (row.Kind == MissionProfileRowKind.Empty) return emptyBodyCellLabel;
            return IsStrongRow(row) ? strongCellLabel : cellStyle;
        }

        /// <summary>The Summary block and the vessel-line headers read bold. Pure.</summary>
        internal static bool IsStrongRow(MissionProfileRow row)
            => row != null && (row.Kind == MissionProfileRowKind.Summary || row.Kind == MissionProfileRowKind.Line);

        /// <summary>Spent holds a figure (recorded, or the <c>~</c> estimate): the house
        /// orange; an empty or <c>-</c> cell keeps the normal colour (design 2.3). Pure.</summary>
        internal static bool DrawsSpentInOrange(MissionProfileRow row)
            => row != null && row.HasSpent && !string.IsNullOrEmpty(row.Spent);

        private void Close()
        {
            isOpen = false;
            ReleaseInputLock();
            ParsekLog.Verbose("UI",
                $"Profile window closed: tree={treeId ?? "<null>"} mission={missionId ?? "<none>"}");
        }

        // ------------------------------------------------------------------ lookups

        private static Mission FindMission(string id)
            => string.IsNullOrEmpty(id) ? null : MissionStore.FindById(id);

        private static RecordingTree FindTree(string id)
        {
            if (string.IsNullOrEmpty(id)) return null;
            var trees = RecordingStore.CommittedTrees;
            for (int i = 0; i < trees.Count; i++)
                if (trees[i] != null && string.Equals(trees[i].Id, id, StringComparison.Ordinal))
                    return trees[i];
            return null;
        }

        // The first row's date: the Log's formatter.
        // The window's date cell. The gallery's mock profiles use it too, so a mocked capture
        // prints the calendar string the game would; the headless unit suite (no KSP runtime)
        // gets the invariant UT number instead.
        internal static string FormatDate(double ut)
        {
            try { return KSPUtil.PrintDateCompact(ut, true); }
            catch
            {
                return ut.ToString("F0", System.Globalization.CultureInfo.InvariantCulture);
            }
        }
    }
}
