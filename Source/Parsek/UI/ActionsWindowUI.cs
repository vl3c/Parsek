using System;
using System.Collections.Generic;
using ClickThroughFix;
using UnityEngine;

namespace Parsek
{
    /// <summary>
    /// Game Actions window extracted from ParsekUI.
    /// Shows ledger actions, recorded/uncommitted game state events, resource budget, and retired kerbals.
    /// </summary>
    internal class ActionsWindowUI
    {
        private readonly ParsekUI parentUI;

        // Actions window
        private bool showActionsWindow;
        private Rect actionsWindowRect;
        private Vector2 actionsScrollPos;
        private bool isResizingActionsWindow;
        private bool actionsWindowHasInputLock;
        private const string ActionsInputLockId = "Parsek_ActionsWindow";
        private int lastRetiredKerbalCount = -1; // for change-based logging

        // Cached styles for actions window
        private GUIStyle actionsGrayStyle;
        private GUIStyle actionsWhiteStyle;
        private GUIStyle actionsGreenStyle;
        private GUIStyle actionsRedStyle;

        // Actions table sort state
        private enum ActionsSortColumn { Time, Type, Description, Status }
        private ActionsSortColumn actionsSortColumn = ActionsSortColumn.Time;
        private bool actionsSortAscending = true;

        // Window drag tracking for position logging
        private Rect lastActionsWindowRect;

        private const float MinWindowWidth = 350f;
        private const float MinWindowHeight = 150f;

        public bool IsOpen
        {
            get { return showActionsWindow; }
            set { showActionsWindow = value; }
        }

        internal ActionsWindowUI(ParsekUI parentUI)
        {
            this.parentUI = parentUI;
        }

        public void DrawIfOpen(Rect mainWindowRect)
        {
            if (!showActionsWindow)
            {
                ReleaseInputLock();
                return;
            }

            // Position to the left of main window on first open
            if (actionsWindowRect.width < 1f)
            {
                float x = mainWindowRect.x - 538;
                if (x < 0) x = mainWindowRect.x + mainWindowRect.width + 10;
                actionsWindowRect = new Rect(x, mainWindowRect.y, 528, mainWindowRect.height);
                var ic = System.Globalization.CultureInfo.InvariantCulture;
                ParsekLog.Verbose("UI", $"Actions window initial position: x={x.ToString("F0", ic)} y={mainWindowRect.y.ToString("F0", ic)} (mainWindow.x={mainWindowRect.x.ToString("F0", ic)})");
            }

            ParsekUI.HandleResizeDrag(ref actionsWindowRect, ref isResizingActionsWindow,
                MinWindowWidth, MinWindowHeight, "Actions window");

            var opaqueWindowStyle = parentUI.GetOpaqueWindowStyle();
            actionsWindowRect = ClickThruBlocker.GUILayoutWindow(
                "ParsekActions".GetHashCode(),
                actionsWindowRect,
                DrawActionsWindow,
                "Parsek \u2014 Game Actions",
                opaqueWindowStyle,
                GUILayout.Width(actionsWindowRect.width),
                GUILayout.Height(actionsWindowRect.height)
            );
            parentUI.LogWindowPosition("Actions", ref lastActionsWindowRect, actionsWindowRect);

            if (actionsWindowRect.Contains(Event.current.mousePosition))
            {
                if (!actionsWindowHasInputLock)
                {
                    InputLockManager.SetControlLock(ControlTypes.CAMERACONTROLS, ActionsInputLockId);
                    actionsWindowHasInputLock = true;
                }
            }
            else
            {
                ReleaseInputLock();
            }
        }

        public void ReleaseInputLock()
        {
            if (!actionsWindowHasInputLock) return;
            InputLockManager.RemoveControlLock(ActionsInputLockId);
            actionsWindowHasInputLock = false;
        }

        private void EnsureActionsStyles()
        {
            if (actionsGrayStyle != null) return;

            actionsGrayStyle = new GUIStyle(GUI.skin.label);
            actionsGrayStyle.normal.textColor = Color.gray;

            actionsWhiteStyle = new GUIStyle(GUI.skin.label);
            actionsWhiteStyle.normal.textColor = Color.white;

            actionsGreenStyle = new GUIStyle(GUI.skin.label);
            actionsGreenStyle.normal.textColor = new Color(0.5f, 1f, 0.5f);

            actionsRedStyle = new GUIStyle(GUI.skin.label);
            actionsRedStyle.normal.textColor = new Color(1f, 0.5f, 0.5f);
        }

        private void DrawLedgerActionsSection(IReadOnlyList<GameAction> ledgerActions)
        {
            GUILayout.Label($"Ledger Actions ({ledgerActions.Count})", GUI.skin.box);

            // Sorted by UT descending (newest first)
            for (int i = ledgerActions.Count - 1; i >= 0; i--)
            {
                var action = ledgerActions[i];
                Color color = GameActionDisplay.GetColor(action.Type);
                GUIStyle style;
                if (color.g > 0.9f && color.r < 0.6f)
                    style = actionsGreenStyle;
                else if (color.r > 0.9f && color.g < 0.6f)
                    style = actionsRedStyle;
                else
                    style = actionsWhiteStyle;

                GUILayout.BeginHorizontal();

                string time = KSPUtil.PrintDateCompact(action.UT, true);
                GUILayout.Label(time, style, GUILayout.Width(90));

                string category = GameActionDisplay.GetCategory(action.Type);
                GUILayout.Label(category, style, GUILayout.Width(65));

                string desc = GameActionDisplay.GetDescription(action);
                GUILayout.Label(desc, style, GUILayout.ExpandWidth(true));

                GUILayout.EndHorizontal();
            }
        }

        private void DrawRetiredKerbalsSection()
        {
            var retiredKerbals = LedgerOrchestrator.Kerbals?.GetRetiredKerbals() ?? new List<string>();
            if (retiredKerbals.Count > 0)
            {
                if (retiredKerbals.Count != lastRetiredKerbalCount)
                {
                    ParsekLog.Verbose("UI",
                        $"Retired kerbals count changed: {lastRetiredKerbalCount} -> {retiredKerbals.Count}");
                    lastRetiredKerbalCount = retiredKerbals.Count;
                }

                GUILayout.Space(5);
                GUILayout.Label($"Retired Stand-ins ({retiredKerbals.Count})", GUI.skin.box);
                GUILayout.BeginVertical(GUI.skin.box);
                for (int i = 0; i < retiredKerbals.Count; i++)
                {
                    GUILayout.Label(retiredKerbals[i], actionsGrayStyle);
                }
                GUILayout.EndVertical();
            }
            else if (lastRetiredKerbalCount > 0)
            {
                ParsekLog.Verbose("UI", "Retired kerbals list cleared");
                lastRetiredKerbalCount = 0;
            }
        }

        private void DrawActionsWindow(int windowID)
        {
            EnsureActionsStyles();

            // A. Resource Budget Summary
            DrawResourceBudget();

            // B. Recorded Actions List + C. Uncommitted events
            List<Tuple<GameStateEvent, bool>> allEvents;
            List<GameStateEvent> uncommittedEvents;
            BuildSortedActionEvents(actionsSortColumn, actionsSortAscending,
                out allEvents, out uncommittedEvents);

            // Single scroll view for all sections
            bool hasCommitted = allEvents.Count > 0;
            bool hasUncommitted = uncommittedEvents.Count > 0;
            var ledgerActions = Ledger.Actions;
            bool hasLedger = ledgerActions.Count > 0;

            if (hasLedger || hasCommitted || hasUncommitted)
            {
                GUILayout.Space(5);

                // Column headers (clickable to sort)
                GUILayout.BeginHorizontal();
                if (GUILayout.Button(SortHeader("Time", ActionsSortColumn.Time), GUI.skin.label, GUILayout.Width(90)))
                    ToggleActionsSort(ActionsSortColumn.Time);
                if (GUILayout.Button(SortHeader("Type", ActionsSortColumn.Type), GUI.skin.label, GUILayout.Width(65)))
                    ToggleActionsSort(ActionsSortColumn.Type);
                if (GUILayout.Button(SortHeader("Description", ActionsSortColumn.Description), GUI.skin.label, GUILayout.ExpandWidth(true)))
                    ToggleActionsSort(ActionsSortColumn.Description);
                if (GUILayout.Button(SortHeader("Status", ActionsSortColumn.Status), GUI.skin.label, GUILayout.Width(55)))
                    ToggleActionsSort(ActionsSortColumn.Status);
                GUILayout.Space(25); // space for delete column
                GUILayout.EndHorizontal();

                GameStateEvent? eventToDeleteCommitted = null;
                GameStateEvent? eventToDeleteUncommitted = null;

                actionsScrollPos = GUILayout.BeginScrollView(actionsScrollPos, GUILayout.ExpandHeight(true));

                if (hasLedger)
                    DrawLedgerActionsSection(ledgerActions);

                // --- Recorded Actions (legacy game state events) ---
                if (hasCommitted)
                {
                    if (hasLedger)
                        GUILayout.Space(5);
                    GUILayout.Label("Recorded Actions", GUI.skin.box);

                    for (int i = 0; i < allEvents.Count; i++)
                    {
                        var e = allEvents[i].Item1;
                        bool replayed = allEvents[i].Item2;
                        GUIStyle style = replayed ? actionsGrayStyle : actionsWhiteStyle;

                        GUILayout.BeginHorizontal();

                        string time = KSPUtil.PrintDateCompact(e.ut, true);
                        GUILayout.Label(time, style, GUILayout.Width(90));

                        string category = GameStateEventDisplay.GetDisplayCategory(e.eventType);
                        GUILayout.Label(category, style, GUILayout.Width(65));

                        string desc = GameStateEventDisplay.GetDisplayDescription(e);
                        GUILayout.Label(desc, style, GUILayout.ExpandWidth(true));

                        string status = replayed ? "Replayed" : "Pending";
                        GUILayout.Label(status, style, GUILayout.Width(55));

                        if (GUILayout.Button("x", GUILayout.Width(20)))
                            eventToDeleteCommitted = e;

                        GUILayout.EndHorizontal();
                    }
                }

                if (hasUncommitted)
                {
                    if (hasCommitted || hasLedger)
                        GUILayout.Space(5);
                    GUILayout.Label("Uncommitted", GUI.skin.box);

                    for (int i = 0; i < uncommittedEvents.Count; i++)
                    {
                        var e = uncommittedEvents[i];

                        GUILayout.BeginHorizontal();

                        string time = KSPUtil.PrintDateCompact(e.ut, true);
                        GUILayout.Label(time, actionsWhiteStyle, GUILayout.Width(90));

                        string category = GameStateEventDisplay.GetDisplayCategory(e.eventType);
                        GUILayout.Label(category, actionsWhiteStyle, GUILayout.Width(65));

                        string desc = GameStateEventDisplay.GetDisplayDescription(e);
                        GUILayout.Label(desc, actionsWhiteStyle, GUILayout.ExpandWidth(true));

                        GUILayout.Label("\u2014", actionsGrayStyle, GUILayout.Width(55)); // em dash

                        if (GUILayout.Button("x", GUILayout.Width(20)))
                            eventToDeleteUncommitted = e;

                        GUILayout.EndHorizontal();
                    }
                }

                GUILayout.EndScrollView();

                // Process deletions outside the iteration loop
                if (eventToDeleteCommitted.HasValue)
                {
                    var del = eventToDeleteCommitted.Value;
                    ParsekLog.Verbose("UI", $"Delete committed action: {del.eventType} key='{del.key}' ut={del.ut:F1}");
                    MilestoneStore.RemoveCommittedEvent(del);
                }
                if (eventToDeleteUncommitted.HasValue)
                {
                    var del = eventToDeleteUncommitted.Value;
                    ParsekLog.Verbose("UI", $"Delete uncommitted action: {del.eventType} key='{del.key}' ut={del.ut:F1}");
                    GameStateStore.RemoveEvent(del);
                }
            }
            else
            {
                GUILayout.Space(5);
                GUILayout.Label("No actions recorded.");
            }

            DrawRetiredKerbalsSection();

            // C. Bottom Bar — pinned to window bottom
            GUILayout.FlexibleSpace();

            uint epoch = MilestoneStore.CurrentEpoch;
            if (epoch > 0)
            {
                GUILayout.Label($"Epoch: {epoch} ({epoch} revert{(epoch == 1 ? "" : "s")})",
                    actionsGrayStyle);
            }

            if (GUILayout.Button("Close"))
            {
                showActionsWindow = false;
                ParsekLog.Verbose("UI", "Actions window closed via button");
            }

            ParsekUI.DrawResizeHandle(actionsWindowRect, ref isResizingActionsWindow,
                "Actions window");

            GUI.DragWindow();
        }

        private string SortHeader(string label, ActionsSortColumn column)
        {
            if (actionsSortColumn == column)
                return label + (actionsSortAscending ? " \u25b2" : " \u25bc"); // ▲ / ▼
            return label;
        }

        private void ToggleActionsSort(ActionsSortColumn column)
        {
            if (actionsSortColumn == column)
                actionsSortAscending = !actionsSortAscending;
            else
            {
                actionsSortColumn = column;
                actionsSortAscending = true;
            }
            ParsekLog.Verbose("UI", $"Actions sort changed: column={column} ascending={actionsSortAscending}");
        }

        /// <summary>
        /// Builds the sorted list of committed action events and the list of uncommitted events
        /// for the actions window.
        /// </summary>
        private static void BuildSortedActionEvents(
            ActionsSortColumn sortColumn, bool sortAscending,
            out List<Tuple<GameStateEvent, bool>> allEvents,
            out List<GameStateEvent> uncommittedEvents)
        {
            var milestones = MilestoneStore.Milestones;
            uint currentEpoch = MilestoneStore.CurrentEpoch;

            allEvents = new List<Tuple<GameStateEvent, bool>>();
            for (int i = 0; i < milestones.Count; i++)
            {
                var m = milestones[i];
                if (!m.Committed || m.Epoch != currentEpoch) continue;
                for (int j = 0; j < m.Events.Count; j++)
                {
                    if (GameStateStore.IsMilestoneFilteredEvent(m.Events[j].eventType))
                        continue;
                    bool replayed = j <= m.LastReplayedEventIndex;
                    allEvents.Add(Tuple.Create(m.Events[j], replayed));
                }
            }

            // Sort events
            switch (sortColumn)
            {
                case ActionsSortColumn.Time:
                    allEvents.Sort((a, b) => sortAscending
                        ? a.Item1.ut.CompareTo(b.Item1.ut)
                        : b.Item1.ut.CompareTo(a.Item1.ut));
                    break;
                case ActionsSortColumn.Type:
                    allEvents.Sort((a, b) =>
                    {
                        int cmp = string.Compare(
                            GameStateEventDisplay.GetDisplayCategory(a.Item1.eventType),
                            GameStateEventDisplay.GetDisplayCategory(b.Item1.eventType),
                            StringComparison.Ordinal);
                        return sortAscending ? cmp : -cmp;
                    });
                    break;
                case ActionsSortColumn.Description:
                    allEvents.Sort((a, b) =>
                    {
                        int cmp = string.Compare(
                            GameStateEventDisplay.GetDisplayDescription(a.Item1),
                            GameStateEventDisplay.GetDisplayDescription(b.Item1),
                            StringComparison.Ordinal);
                        return sortAscending ? cmp : -cmp;
                    });
                    break;
                case ActionsSortColumn.Status:
                    allEvents.Sort((a, b) =>
                    {
                        int cmp = a.Item2.CompareTo(b.Item2); // false (Pending) < true (Replayed)
                        return sortAscending ? cmp : -cmp;
                    });
                    break;
            }

            // Uncommitted events (not yet in any milestone)
            double lastMilestoneEndUT = 0;
            for (int i = 0; i < milestones.Count; i++)
            {
                if (milestones[i].Epoch == currentEpoch && milestones[i].EndUT > lastMilestoneEndUT)
                    lastMilestoneEndUT = milestones[i].EndUT;
            }

            var storeEvents = GameStateStore.Events;
            uncommittedEvents = new List<GameStateEvent>();
            for (int i = 0; i < storeEvents.Count; i++)
            {
                var e = storeEvents[i];
                if (e.epoch != currentEpoch) continue;
                if (e.ut <= lastMilestoneEndUT) continue;
                if (GameStateStore.IsMilestoneFilteredEvent(e.eventType)) continue;
                uncommittedEvents.Add(e);
            }
            uncommittedEvents.Sort((a, b) => a.ut.CompareTo(b.ut));
        }

        private void DrawResourceBudget()
        {
            var budget = parentUI.GetCachedBudget();

            if (budget.reservedFunds <= 0 && budget.reservedScience <= 0 && budget.reservedReputation <= 0)
                return;

            var ic = System.Globalization.CultureInfo.InvariantCulture;
            GUILayout.Space(5);
            GUILayout.Label("Resources", GUI.skin.box);

            bool anyOverCommitted = false;

            if (budget.reservedFunds > 0)
            {
                double currentFunds = 0;
                try { if (Funding.Instance != null) currentFunds = Funding.Instance.Funds; } catch { }
                anyOverCommitted |= DrawResourceLine("Funds", currentFunds, budget.reservedFunds, "N0", ic);
            }

            if (budget.reservedScience > 0)
            {
                double currentScience = 0;
                try { if (ResearchAndDevelopment.Instance != null) currentScience = ResearchAndDevelopment.Instance.Science; } catch { }
                anyOverCommitted |= DrawResourceLine("Science", currentScience, budget.reservedScience, "F1", ic);
            }

            if (budget.reservedReputation > 0)
            {
                float currentRep = 0;
                try { if (Reputation.Instance != null) currentRep = Reputation.Instance.reputation; } catch { }
                anyOverCommitted |= DrawResourceLine("Reputation", (double)currentRep, budget.reservedReputation, "F0", ic);
            }

            if (anyOverCommitted)
            {
                Color prev = GUI.contentColor;
                GUI.contentColor = Color.yellow;
                GUILayout.Label("Over-committed! Some timeline actions may fail.");
                GUI.contentColor = prev;
            }
        }

        /// <summary>
        /// Draws a single resource budget line (funds, science, or reputation).
        /// Returns true if the resource is over-committed.
        /// </summary>
        private static bool DrawResourceLine(string label, double currentAmount, double reserved,
            string format, System.Globalization.CultureInfo ic)
        {
            double available = currentAmount - reserved;
            double total = currentAmount;
            bool over = available < 0;
            Color prev = GUI.contentColor;
            if (over) GUI.contentColor = Color.red;
            GUILayout.Label($"{label}: {available.ToString(format, ic)} available to use ({reserved.ToString(format, ic)} committed out of {total.ToString(format, ic)} total)");
            GUI.contentColor = prev;
            return over;
        }
    }
}