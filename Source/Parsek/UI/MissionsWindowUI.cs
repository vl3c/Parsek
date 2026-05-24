using System.Collections.Generic;
using ClickThroughFix;
using UnityEngine;

namespace Parsek
{
    /// <summary>
    /// The Missions window. A standalone class that reuses <see cref="RecordingsTableUI"/>'s
    /// visual style and rendering helpers (column-header style, dark table-body box,
    /// tree-branch connector glyphs, expand/collapse carets, section-header bar) without
    /// inheriting it. Unlike the recordings window (which groups individual recordings:
    /// groups -> chains -> recordings), this groups the higher mission abstraction: it
    /// lists saved <see cref="Mission"/>s (from <see cref="MissionStore"/>), and renders
    /// each one's tree as collapsed continuous-vessel through-lines (from
    /// <see cref="MissionThroughLineBuilder"/>) with the offshoots that left them as
    /// children. Per Mission: a name + Clone/Delete, and include checkboxes bound to that
    /// Mission's selection (persisted). Individual recording legs never appear here.
    /// Not yet wired: looping a Mission as a unit. See docs/dev/design-mission-abstractions.md.
    /// </summary>
    internal class MissionsWindowUI
    {
        private readonly ParsekUI parentUI;

        private bool showWindow;
        private Rect windowRect;
        private Rect lastWindowRect;
        private Vector2 scrollPos;
        private bool isResizing;
        private bool hasInputLock;

        // Collapsed through-line heads, keyed "missionId:headId" so two Missions over
        // the same tree collapse independently. Transient UI state (not persisted). The
        // include selection lives per-Mission in Mission.ExcludedThroughLineHeadIds.
        private readonly HashSet<string> collapsedLegs = new HashSet<string>();

        // Per-Mission loop-period edit buffer, keyed by Mission.Id. Transient UI state:
        // holds the in-progress text while the player types, committed on field change.
        private readonly Dictionary<string, string> loopPeriodEditBuffers =
            new Dictionary<string, string>();

        // Per-frame cache of the derived mission read model (structure + through-line
        // view) keyed by tree id. OnGUI fires several times per frame (Layout, Repaint,
        // input events) and multiple missions can target the same tree, so without this
        // the two builders ran 6-12 times per frame for unchanged trees. The cache is
        // cleared whenever a new frame starts, so it always reflects the current trees
        // (no invalidation logic, no staleness) and the Layout and Repaint passes of one
        // frame see identical data.
        private readonly Dictionary<string, (MissionStructure structure, MissionThroughLineView view)>
            missionViewCache =
                new Dictionary<string, (MissionStructure structure, MissionThroughLineView view)>();
        private int missionViewCacheFrame = -1;

        private const string InputLockId = "Parsek_MissionsWindow";
        private const float MinWindowWidth = 450f;
        private const float MinWindowHeight = 150f;
        private const float DefaultWidth = 680f;

        // Fixed-width columns to the right of the expanding "Vessel" column, mirroring
        // the recordings window's fixed-width cells so the window reads as the same
        // table style. Layout: [check] Vessel | Start time | Start event | End event | End time.
        private const float ColW_Check = 22f;
        private const float ColW_StartTime = 100f;
        private const float ColW_StartEvent = 85f;
        private const float ColW_EndEvent = 85f;
        private const float ColW_EndTime = 100f;
        private const float ColW_Action = 60f;

        // Mission-header loop controls (live on the header row, not the table columns).
        private const float ColW_Loop = 52f;
        private const float ColW_Period = 90f;

        private static readonly Color DimColor = new Color(1f, 1f, 1f, 0.45f);

        // -- Recreated styles --
        // RecordingsTableUI builds these privately inside EnsurePhaseStyles();
        // we cannot reach those instances from a subclass, so we lazily rebuild
        // the same styles here (verbatim, adapted) to match the look exactly.
        private GUIStyle bodyCellLabel;
        private GUIStyle tableBodyBoxStyle;
        // Zero-horizontal-margin field/button styles for the loop-period cell, identical to
        // the recordings window's bodyCellTextFieldFlush / bodyCellButtonFlush so the value
        // box and unit button render the same as RecordingsTableUI.DrawLoopPeriodCell.
        private GUIStyle bodyCellTextFieldFlush;
        private GUIStyle bodyCellButtonFlush;

        // Column-header text inset (matches RecordingsTableUI.BodyCellTextIndent so
        // body labels land under their header text).
        private const int BodyCellTextIndent = 5;

        // -- Caret glyphs (built from char codes so the source stays ASCII, the
        // same way RecordingsTableUI builds its TreeConnector glyphs) --
        // U+25BC down caret (expanded), U+25B6 right caret (collapsed),
        // U+21B3 downward-rightward arrow (merge reference row).
        private static readonly string CaretDown =
            new string(new[] { (char)0x25BC, ' ' });
        private static readonly string CaretRight =
            new string(new[] { (char)0x25B6, ' ' });
        private static readonly string MergeArrow =
            new string(new[] { (char)0x21B3, ' ' });

        public MissionsWindowUI(ParsekUI parentUI)
        {
            this.parentUI = parentUI;
        }

        public bool IsOpen
        {
            get { return showWindow; }
            set { showWindow = value; }
        }

        public void DrawIfOpen(Rect mainWindowRect)
        {
            if (!showWindow)
            {
                ReleaseInputLock();
                return;
            }

            if (windowRect.width < 1f)
            {
                float h = parentUI.InFlightMode ? mainWindowRect.height : mainWindowRect.height * 2f;
                windowRect = new Rect(
                    mainWindowRect.x + mainWindowRect.width + 10f,
                    mainWindowRect.y,
                    DefaultWidth, h);
            }

            ParsekUI.HandleResizeDrag(ref windowRect, ref isResizing,
                MinWindowWidth, MinWindowHeight, "Missions window");

            var opaqueWindowStyle = parentUI.GetOpaqueWindowStyle();
            if (opaqueWindowStyle == null)
                return;

            ParsekUI.ResetWindowGuiColors(out Color prevColor, out Color prevBackground, out Color prevContent);
            try
            {
                windowRect = ClickThruBlocker.GUILayoutWindow(
                    "ParsekMissions".GetHashCode(),
                    windowRect,
                    DrawWindow,
                    "Parsek - Missions",
                    opaqueWindowStyle,
                    GUILayout.Width(windowRect.width),
                    GUILayout.Height(windowRect.height));
            }
            finally
            {
                ParsekUI.RestoreWindowGuiColors(prevColor, prevBackground, prevContent);
            }

            parentUI.LogWindowPosition("Missions", ref lastWindowRect, windowRect);

            if (windowRect.Contains(Event.current.mousePosition))
            {
                if (!hasInputLock)
                {
                    InputLockManager.SetControlLock(ControlTypes.CAMERACONTROLS, InputLockId);
                    hasInputLock = true;
                }
            }
            else
            {
                ReleaseInputLock();
            }
        }

        internal void ReleaseInputLock()
        {
            if (!hasInputLock)
                return;
            InputLockManager.RemoveControlLock(InputLockId);
            hasInputLock = false;
        }

        // Lazily rebuilds the body-cell label and dark table-body box styles the
        // recordings window uses. Same definitions as RecordingsTableUI.EnsurePhaseStyles
        // (cell label padding = left-only indent matching the header text inset;
        // body box = dark background, zero horizontal padding so columns align with
        // the header above the scroll view).
        private void EnsureStyles()
        {
            if (bodyCellLabel != null) return;

            var cellLabelPadding = new RectOffset(BodyCellTextIndent, 0, 0, 0);
            bodyCellLabel = new GUIStyle(GUI.skin.label) { padding = cellLabelPadding };

            tableBodyBoxStyle = new GUIStyle(GUI.skin.box)
            {
                padding = new RectOffset(0, 0, 2, 2),
                margin = new RectOffset(0, 0, 0, 0)
            };

            bodyCellButtonFlush = new GUIStyle(GUI.skin.button)
            {
                margin = new RectOffset(
                    0, 0,
                    GUI.skin.button.margin.top, GUI.skin.button.margin.bottom)
            };
            bodyCellTextFieldFlush = new GUIStyle(GUI.skin.textField)
            {
                margin = new RectOffset(
                    0, 0,
                    GUI.skin.textField.margin.top, GUI.skin.textField.margin.bottom)
            };
        }

        private void DrawWindow(int windowID)
        {
            EnsureStyles();

            // Breathing room below the title bar (matches the recordings window).
            GUILayout.Space(5);

            GUILayout.BeginVertical();

            var trees = RecordingStore.CommittedTrees;
            MissionStore.EnsureDefaultsForTrees(trees);
            var missions = MissionStore.Missions;
            if (missions == null || missions.Count == 0)
            {
                GUILayout.Label("No missions recorded yet.");
                GUILayout.FlexibleSpace();
            }
            else
            {
                // Fixed column-header row (outside the scroll view), styled with the
                // recordings window's shared column-header style.
                DrawColumnHeader();

                scrollPos = GUILayout.BeginScrollView(scrollPos, false, true, GUILayout.ExpandHeight(true));

                // Dark list-area background (matches the recordings window) with no
                // horizontal padding so columns align with the header above.
                GUILayout.BeginVertical(tableBodyBoxStyle);

                // Snapshot so Clone / Delete (which mutate the store) can be invoked
                // from within the draw loop; the change takes effect on the next frame.
                var snapshot = new List<Mission>(missions);
                int missionCount = 0;
                int rowCount = 0;
                for (int i = 0; i < snapshot.Count; i++)
                {
                    Mission mission = snapshot[i];
                    if (mission == null)
                        continue;
                    RecordingTree tree = FindTree(trees, mission.TreeId);
                    if (tree == null)
                        continue;
                    missionCount++;

                    var (structure, view) = GetMissionView(tree);

                    DrawMissionHeader(mission);

                    // Single source of truth for the include/exclude cascade (greying +
                    // checkbox state both derive from this set). Computed once per mission
                    // per frame so there is exactly one copy of the rule (MissionSelection).
                    HashSet<string> includedHeads = MissionSelection.ComputeIncludedHeadIds(
                        view, mission.ExcludedThroughLineHeadIds);

                    var visited = new HashSet<string>();
                    for (int r = 0; r < view.RootHeadIds.Count; r++)
                    {
                        bool isLast = r == view.RootHeadIds.Count - 1;
                        rowCount += DrawThroughLine(structure, view, mission, includedHeads, view.RootHeadIds[r], 1, isLast, false, visited);
                    }
                }

                GUILayout.EndVertical();
                GUILayout.EndScrollView();

                ParsekLog.VerboseRateLimited("UI", "missions-window-draw",
                    $"Missions window: missions={missionCount} rows={rowCount}", 5.0);
            }

            // Full-width Close button at the bottom (matches the Timeline window).
            if (GUILayout.Button("Close"))
                IsOpen = false;

            GUILayout.EndVertical();
            ParsekUI.DrawResizeHandle(windowRect, ref isResizing, "Missions window");
            GUI.DragWindow();
        }

        private static RecordingTree FindTree(List<RecordingTree> trees, string treeId)
        {
            if (trees == null || string.IsNullOrEmpty(treeId))
                return null;
            for (int i = 0; i < trees.Count; i++)
                if (trees[i] != null && trees[i].Id == treeId)
                    return trees[i];
            return null;
        }

        // Returns the derived (structure, view) for a tree, building it at most once per
        // frame per tree id. The first lookup in a new frame clears the cache so the data
        // stays fresh; later lookups in the same frame (other OnGUI passes, or other
        // missions targeting the same tree) hit the cache instead of rebuilding.
        private (MissionStructure structure, MissionThroughLineView view) GetMissionView(RecordingTree tree)
        {
            int frame = Time.frameCount;
            if (frame != missionViewCacheFrame)
            {
                missionViewCache.Clear();
                missionViewCacheFrame = frame;
            }

            if (!missionViewCache.TryGetValue(tree.Id, out var cached))
            {
                MissionStructure structure = MissionStructureBuilder.Build(tree);
                MissionThroughLineView view = MissionThroughLineBuilder.Build(structure);
                cached = (structure, view);
                missionViewCache[tree.Id] = cached;
            }

            return cached;
        }

        // Mission header bar: the mission name (section-header style), a loop toggle + a
        // loop-period cell, then Clone and Delete. Delete is disabled when this is the
        // tree's last mission. Clone/Delete mutate MissionStore; the draw loop iterates a
        // snapshot so that is safe. The loop toggle enforces single-selection through
        // MissionStore.SetLoopEnabled (only flips bools on other missions, never
        // adds/removes, so it is safe to call from inside the draw loop). The loop
        // controls are INERT for now: they persist state but do not yet drive playback
        // (the looping engine is wired in a later phase).
        private void DrawMissionHeader(Mission mission)
        {
            GUILayout.BeginHorizontal();
            GUILayout.Label(string.IsNullOrEmpty(mission.Name) ? "(mission)" : mission.Name,
                parentUI.GetSectionHeaderStyle(), GUILayout.ExpandWidth(true));

            bool loopNow = GUILayout.Toggle(mission.LoopPlayback, "Loop", GUILayout.Width(ColW_Loop));
            if (loopNow != mission.LoopPlayback)
                MissionStore.SetLoopEnabled(mission, loopNow, Planetarium.GetUniversalTime());

            DrawMissionLoopPeriodCell(mission);

            if (GUILayout.Button("Clone", GUILayout.Width(ColW_Action)))
                MissionStore.Clone(mission);
            GUI.enabled = MissionStore.CanDelete(mission);
            if (GUILayout.Button("Delete", GUILayout.Width(ColW_Action)))
                MissionStore.Delete(mission);
            GUI.enabled = true;
            GUILayout.EndHorizontal();
        }

        // Loop-period cell: a value text field plus a unit button cycling Sec/Min/Hour/
        // Auto, mirroring the recordings window's DrawLoopPeriodCell. When loop is off the
        // controls grey out (matching the recordings window). Reuses ParsekUI's shared
        // loop-time helpers so the parse/format/convert rules stay identical. Commit on
        // text change: parse via TryParseLoopInput, reject negatives, clamp below
        // MinCycleDuration (same contract as RecordingsTableUI.CommitLoopPeriodEdit).
        private void DrawMissionLoopPeriodCell(Mission mission)
        {
            const float unitBtnW = 40f; // match RecordingsTableUI.DrawLoopPeriodCell
            float valueW = ColW_Period - unitBtnW - 4f;

            bool enabled = mission.LoopPlayback;
            bool auto = mission.LoopTimeUnit == LoopTimeUnit.Auto;

            string controlName = "MissionLoopPeriod_" + mission.Id;

            if (auto)
            {
                // Auto mode: the value is inherited from Settings > Looping, not the
                // Mission's own LoopIntervalSeconds. Mirror RecordingsTableUI's auto
                // branch: show the global value in the global display unit plus an
                // explicit unit suffix (the unit button reads "auto", so the suffix is
                // what tells the player the real unit). Field is non-editable.
                var settings = ParsekSettings.Current;
                double globalVal = settings != null
                    ? ParsekUI.ConvertFromSeconds(settings.autoLoopIntervalSeconds, settings.AutoLoopDisplayUnit)
                    : LoopTiming.DefaultLoopIntervalSeconds;
                var globalDisplayUnit = settings != null ? settings.AutoLoopDisplayUnit : LoopTimeUnit.Sec;
                string autoText = ParsekUI.FormatLoopValue(globalVal, globalDisplayUnit)
                    + ParsekUI.UnitSuffix(globalDisplayUnit);
                // Drop any stale edit buffer so re-entering a manual unit re-seeds cleanly.
                loopPeriodEditBuffers.Remove(mission.Id);
                GUI.enabled = false;
                GUILayout.TextField(autoText, bodyCellTextFieldFlush, GUILayout.Width(valueW));
                GUI.enabled = true;
            }
            else
            {
                // Manual mode: seed / refresh the per-mission edit buffer from the stored
                // value whenever the field is not actively being typed into.
                bool focused = GUI.GetNameOfFocusedControl() == controlName;
                if (!focused || !loopPeriodEditBuffers.ContainsKey(mission.Id))
                {
                    loopPeriodEditBuffers[mission.Id] = ParsekUI.FormatLoopValue(
                        ParsekUI.ConvertFromSeconds(mission.LoopIntervalSeconds, mission.LoopTimeUnit),
                        mission.LoopTimeUnit);
                }

                GUI.enabled = enabled;
                GUI.SetNextControlName(controlName);
                string newText = GUILayout.TextField(
                    loopPeriodEditBuffers[mission.Id], bodyCellTextFieldFlush, GUILayout.Width(valueW));
                if (newText != loopPeriodEditBuffers[mission.Id])
                {
                    loopPeriodEditBuffers[mission.Id] = newText;
                    CommitMissionLoopPeriod(mission, newText);
                }
                GUI.enabled = true;
            }

            GUILayout.Space(4f);
            GUI.enabled = enabled;
            if (GUILayout.Button(ParsekUI.UnitLabel(mission.LoopTimeUnit), bodyCellButtonFlush, GUILayout.Width(unitBtnW)))
            {
                mission.LoopTimeUnit = RecordingsTableUI.CycleRecordingUnit(mission.LoopTimeUnit);
                loopPeriodEditBuffers.Remove(mission.Id);
                GUIUtility.keyboardControl = 0;
                ParsekLog.Info("Mission",
                    $"Loop unit for '{mission.Name}' changed to {mission.LoopTimeUnit}");
            }
            GUI.enabled = true;
        }

        // Parses an edited loop-period value and writes it to the Mission, mirroring
        // RecordingsTableUI.CommitLoopPeriodEdit: reject negatives, clamp below
        // MinCycleDuration. Invalid text is ignored (the field keeps the typed buffer so
        // the player can finish typing). Auto unit does not commit a value.
        private static void CommitMissionLoopPeriod(Mission mission, string text)
        {
            if (mission.LoopTimeUnit == LoopTimeUnit.Auto)
                return;
            var ic = System.Globalization.CultureInfo.InvariantCulture;
            if (!ParsekUI.TryParseLoopInput(text, mission.LoopTimeUnit, out double parsed))
                return;
            double newSeconds = ParsekUI.ConvertToSeconds(parsed, mission.LoopTimeUnit);
            if (newSeconds < 0)
            {
                ParsekLog.Warn("Mission",
                    $"Loop period for '{mission.Name}' rejected: " +
                    $"negative value {newSeconds.ToString("F1", ic)}s");
                return;
            }
            if (newSeconds < LoopTiming.MinCycleDuration)
            {
                ParsekLog.Info("Mission",
                    $"Loop period for '{mission.Name}' clamped from " +
                    $"{newSeconds.ToString("F1", ic)}s to " +
                    $"{LoopTiming.MinCycleDuration.ToString("F1", ic)}s (MinCycleDuration)");
                newSeconds = LoopTiming.MinCycleDuration;
            }
            mission.LoopIntervalSeconds = newSeconds;
            ParsekLog.Info("Mission",
                $"Loop period for '{mission.Name}' updated to " +
                newSeconds.ToString("F1", ic) + "s");
        }

        private static string CollapseKey(Mission mission, string headId)
        {
            return mission.Id + ":" + headId;
        }

        // Column-header row: a checkbox column, a wide expanding "Vessel" column, then
        // Start time / Start event / End event / End time, all in the recordings
        // window's shared column-header style.
        private void DrawColumnHeader()
        {
            var colHdr = parentUI.GetColumnHeaderStyle();
            GUILayout.BeginHorizontal();
            GUILayout.Label("", colHdr, GUILayout.Width(ColW_Check));
            GUILayout.Label("Vessel", colHdr, GUILayout.ExpandWidth(true));
            GUILayout.Label("Start time", colHdr, GUILayout.Width(ColW_StartTime));
            GUILayout.Label("Start event", colHdr, GUILayout.Width(ColW_StartEvent));
            GUILayout.Label("End event", colHdr, GUILayout.Width(ColW_EndEvent));
            GUILayout.Label("End time", colHdr, GUILayout.Width(ColW_EndTime));

            // Reserve the vertical-scrollbar column so the fixed header's right edge
            // aligns with the row cells' right edges (the scroll view always shows a
            // vertical scrollbar). Same trick as the recordings window header.
            float scrollbarWidth = GUI.skin.verticalScrollbar != null
                ? GUI.skin.verticalScrollbar.fixedWidth
                : 16f;
            if (scrollbarWidth <= 0f) scrollbarWidth = 16f;
            GUILayout.Space(scrollbarWidth);

            GUILayout.EndHorizontal();
        }

        // Renders one leg, then recurses into its children. A leg's children, in
        // order, are: its SequenceNextId (if non-null) followed by its
        // BranchChildIds. Mirrors how RecordingsTableUI renders a group/chain tree
        // row (SelfConnectorIndent + TreeConnector + caret + label + fixed cells).
        // The visited set both prevents re-rendering a merge child reached from a
        // second parent (it gets a reference row instead) and guards malformed cycles.
        // Renders one through-line (a collapsed continuous vessel), then recurses into
        // its offshoots (the things that left it: EVA kerbals, decoupled children,
        // forks to other vessels). The visited set guards merges/cycles.
        private int DrawThroughLine(MissionStructure s, MissionThroughLineView v, Mission mission,
            HashSet<string> includedHeads, string headId, int depth, bool isLast, bool parentExcluded,
            HashSet<string> visited)
        {
            if (headId == null || !v.ByHeadId.TryGetValue(headId, out MissionThroughLine tl))
                return 0;

            if (visited.Contains(headId))
            {
                DrawReferenceRow(tl, depth, isLast);
                return 1;
            }
            visited.Add(headId);

            bool hasChildren = tl.OffshootHeadIds.Count > 0;
            bool collapsed = collapsedLegs.Contains(CollapseKey(mission, headId));

            DrawThroughLineRow(s, mission, tl, depth, isLast, hasChildren, collapsed, parentExcluded);
            int rows = 1;

            // Unchecking a through-line drops it and everything downstream. The shared
            // MissionSelection cascade is the single definition: a head is excluded for its
            // offshoots iff it is not in the included set (= self-unchecked OR ancestor
            // excluded), which is exactly the old parentExcluded || self-unchecked rule.
            bool childExcluded = !includedHeads.Contains(headId);
            if (hasChildren && !collapsed)
            {
                for (int i = 0; i < tl.OffshootHeadIds.Count; i++)
                {
                    bool childIsLast = i == tl.OffshootHeadIds.Count - 1;
                    rows += DrawThroughLine(s, v, mission, includedHeads, tl.OffshootHeadIds[i], depth + 1, childIsLast, childExcluded, visited);
                }
            }

            return rows;
        }

        private void DrawThroughLineRow(MissionStructure s, Mission mission, MissionThroughLine tl,
            int depth, bool isLast, bool hasChildren, bool collapsed, bool parentExcluded)
        {
            GUILayout.BeginHorizontal();

            // Include checkbox, keyed by the through-line's head leg in this Mission's
            // excluded set. Disabled when an ancestor is unchecked (this through-line is
            // downstream of a dropped one).
            bool selfUnchecked = mission.ExcludedThroughLineHeadIds.Contains(tl.HeadLegId);
            bool shownChecked = !parentExcluded && !selfUnchecked;
            GUI.enabled = !parentExcluded;
            bool toggled = GUILayout.Toggle(shownChecked, "", GUILayout.Width(ColW_Check));
            GUI.enabled = true;
            if (!parentExcluded && toggled != shownChecked)
            {
                if (toggled) mission.ExcludedThroughLineHeadIds.Remove(tl.HeadLegId);
                else mission.ExcludedThroughLineHeadIds.Add(tl.HeadLegId);
            }

            Color prevColor = GUI.color;
            if (parentExcluded || selfUnchecked)
                GUI.color = DimColor;

            float indent = RecordingsTableUI.SelfConnectorIndent(depth);
            if (indent > 0f)
                GUILayout.Space(indent);

            string connector = depth > 0 ? RecordingsTableUI.TreeConnector(isLast) : "";
            string caret = hasChildren ? (collapsed ? CaretRight : CaretDown) : "";
            string vessel = string.IsNullOrEmpty(tl.VesselName) ? "(vessel)" : tl.VesselName;
            string wide = connector + caret + vessel;

            if (hasChildren)
            {
                if (GUILayout.Button(wide, bodyCellLabel, GUILayout.ExpandWidth(true)))
                {
                    string key = CollapseKey(mission, tl.HeadLegId);
                    if (collapsedLegs.Contains(key))
                        collapsedLegs.Remove(key);
                    else
                        collapsedLegs.Add(key);
                }
            }
            else
            {
                GUILayout.Label(wide, bodyCellLabel, GUILayout.ExpandWidth(true));
            }

            // Start event from the head leg, end event from the tail leg.
            MissionLeg head = s.LegsById.TryGetValue(tl.HeadLegId, out MissionLeg h) ? h : null;
            MissionLeg tail = s.LegsById.TryGetValue(tl.TailLegId, out MissionLeg t2) ? t2 : null;
            GUILayout.Label(KSPUtil.PrintDateCompact(tl.StartUT, true), bodyCellLabel, GUILayout.Width(ColW_StartTime));
            GUILayout.Label(head != null ? StartEventText(head) : "", bodyCellLabel, GUILayout.Width(ColW_StartEvent));
            GUILayout.Label(tail != null ? EndEventText(tail) : "", bodyCellLabel, GUILayout.Width(ColW_EndEvent));
            GUILayout.Label(KSPUtil.PrintDateCompact(tl.EndUT, true), bodyCellLabel, GUILayout.Width(ColW_EndTime));

            GUI.color = prevColor;
            GUILayout.EndHorizontal();
        }

        // Reference row for a through-line reached a second time (a merge): shown once,
        // not recursed into.
        private void DrawReferenceRow(MissionThroughLine tl, int depth, bool isLast)
        {
            GUILayout.BeginHorizontal();
            GUILayout.Label("", bodyCellLabel, GUILayout.Width(ColW_Check));

            float indent = RecordingsTableUI.SelfConnectorIndent(depth);
            if (indent > 0f)
                GUILayout.Space(indent);

            string connector = depth > 0 ? RecordingsTableUI.TreeConnector(isLast) : "";
            string vessel = string.IsNullOrEmpty(tl.VesselName) ? "(vessel)" : tl.VesselName;
            GUILayout.Label($"{connector}{MergeArrow}merges into {vessel} (above)",
                bodyCellLabel, GUILayout.ExpandWidth(true));
            GUILayout.Label("", bodyCellLabel, GUILayout.Width(ColW_StartTime));
            GUILayout.Label("", bodyCellLabel, GUILayout.Width(ColW_StartEvent));
            GUILayout.Label("", bodyCellLabel, GUILayout.Width(ColW_EndEvent));
            GUILayout.Label("", bodyCellLabel, GUILayout.Width(ColW_EndTime));

            GUILayout.EndHorizontal();
        }

        // How a leg began. The continuing primary vessel just "Continues"; only a
        // genuinely new craft gets a start event: an EVA kerbal says "EVA", a
        // separated offshoot (probe / decoupled / broken-off child) says how it
        // separated. EVA is a kerbal's start, never a vessel's start or end.
        private static string StartEventText(MissionLeg leg)
        {
            if (leg.SequencePrevId != null)
                return "Continues";
            if (!string.IsNullOrEmpty(leg.EvaCrewName))
                return "EVA";
            if (leg.IsAnchoredOffshoot && leg.OriginBranchPointType.HasValue)
                return EventLabel(leg.OriginBranchPointType.Value);
            if (leg.IsRoot || !leg.OriginBranchPointType.HasValue)
                return "Launched";
            return "Continues";
        }

        // How a leg ended. A vessel ends with a terminal state (Landed / Destroyed /
        // Recovered / ...). If the same vessel keeps going it "Continues". A branch
        // event (EVA, decouple) is where something LEFT the vessel, not where the
        // vessel ended, so it is never shown as an end.
        private static string EndEventText(MissionLeg leg)
        {
            if (leg.TerminalStateValue.HasValue)
                return leg.TerminalStateValue.Value.ToString();
            if (leg.ContinuesAsVessel)
                return "Continues";
            return "Active";
        }

        private static string EventLabel(BranchPointType type)
        {
            switch (type)
            {
                case BranchPointType.Launch: return "Launched";
                case BranchPointType.Undock: return "Undocked";
                case BranchPointType.Dock: return "Docked";
                case BranchPointType.EVA: return "EVA";
                case BranchPointType.Board: return "Boarded";
                case BranchPointType.JointBreak: return "Decoupled";
                case BranchPointType.Breakup: return "Broke up";
                case BranchPointType.Terminal: return "Ended";
                case BranchPointType.VesselSwitchContinuation: return "Switched";
                default: return type.ToString();
            }
        }
    }
}
