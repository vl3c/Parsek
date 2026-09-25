using System;
using System.Collections.Generic;
using System.Globalization;
using System.Reflection;
using System.Runtime.CompilerServices;
using Contracts;
using KSP.UI;
using KSP.UI.Screens;
using KSP.UI.Screens.Editor;
using UnityEngine;
using Object = UnityEngine.Object;

namespace Parsek.TestCommands
{
    /// <summary>One stock item's decoration as the census records it beside a capture.</summary>
    internal struct StockScreenRecord
    {
        internal string Screen;
        internal string Tab;
        internal string Id;
        internal string Kind;
        internal bool Marked;
        internal bool Blocked;
        internal string Why;

        internal static StockScreenRecord From(StockUiDecoration d)
        {
            return new StockScreenRecord
            {
                Screen = d.Screen.ToString(),
                Tab = d.Tab,
                Id = d.Id,
                Kind = d.Kind.ToString(),
                Marked = d.Marked,
                Blocked = d.Blocked,
                Why = d.Why,
            };
        }
    }

    /// <summary>One stock control's state at capture time.</summary>
    internal struct StockScreenControl
    {
        internal string Screen;
        internal string Name;
        internal string State;
        internal bool Interactable;
        internal bool Visible;
    }

    /// <summary>
    /// The decoration records of every stock screen open at capture time, logged by
    /// <c>CaptureScreenshot</c> next to its <c>capturescreenshot ok label=</c> line so a GUI
    /// mirror can show what Parsek decided beside the photograph. The lines are
    /// <c>[StockUiOverlay] record label=&lt;l&gt; ...</c>, the shape
    /// <c>harness/tools/gui_mirror.py</c> reads beside the <c>decorate</c> pass lines (same
    /// keys; the item id runs to the next key, so a kerbal's name keeps its space). Each record is read from
    /// the SAME decision the screen's own decoration reads (<see cref="StockUiLiveSnapshot"/>,
    /// <see cref="MissionControlStockUi.DecideNow"/>, <see cref="StockUiCrewDialogDecoration.DescribeCurrent"/>,
    /// <see cref="StockUiDecorationQuery.ForFacilityMenu"/>, the strategy gate and
    /// <see cref="StockUiPartPurchase.DecideLive"/>); nothing here reads the drawn text, so a
    /// record that says marked beside a photograph that shows nothing is an overlay finding.
    /// </summary>
    internal static class StockScreenRecords
    {
        /// <summary>The subsystem tag the stock-screen decoration lines print under.</summary>
        internal const string Tag = "StockUiOverlay";
        internal const string LinePrefix = "record";

        /// <summary>One line per screen and tab: <c>items= marked= blocked=</c>.</summary>
        internal static string FormatSummaryLine(string label, string screen, string tab, int items, int marked, int blocked)
        {
            var ic = CultureInfo.InvariantCulture;
            return LinePrefix + " label=" + label
                + " screen=" + screen
                + " tab=" + (string.IsNullOrEmpty(tab) ? "-" : tab)
                + " items=" + items.ToString(ic)
                + " marked=" + marked.ToString(ic)
                + " blocked=" + blocked.ToString(ic);
        }

        /// <summary>One line per marked or blocked item, with the explanation.</summary>
        internal static string FormatItemLine(string label, StockScreenRecord r)
        {
            return LinePrefix + " label=" + label
                + " screen=" + r.Screen
                + " tab=" + (string.IsNullOrEmpty(r.Tab) ? "-" : r.Tab)
                + " item=" + (string.IsNullOrEmpty(r.Id) ? "-" : r.Id)
                + " kind=" + (r.Kind ?? "None")
                + " marked=" + (r.Marked ? "true" : "false")
                + " blocked=" + (r.Blocked ? "true" : "false")
                + " why=\"" + Flatten(r.Why) + "\"";
        }

        /// <summary>The line written when no stock screen is open.</summary>
        internal static string FormatNoneLine(string label)
        {
            return LinePrefix + " label=" + label + " screens=none";
        }

        /// <summary>A multi-line explanation on one log line.</summary>
        internal static string Flatten(string why)
        {
            if (string.IsNullOrEmpty(why)) return "";
            return why.Replace("\r", "").Replace("\n", " | ").Replace("\"", "'");
        }

        /// <summary>
        /// The summary and item lines for a set of records, grouped by screen and tab in
        /// first-seen order. Pure.
        /// </summary>
        internal static List<string> FormatLines(string label, IList<StockScreenRecord> records)
        {
            var lines = new List<string>();
            if (records == null || records.Count == 0)
            {
                lines.Add(FormatNoneLine(label));
                return lines;
            }
            var order = new List<KeyValuePair<string, string>>();
            for (int i = 0; i < records.Count; i++)
            {
                var key = new KeyValuePair<string, string>(records[i].Screen, records[i].Tab);
                if (!order.Contains(key)) order.Add(key);
            }
            foreach (var key in order)
            {
                int items = 0, marked = 0, blocked = 0;
                for (int i = 0; i < records.Count; i++)
                {
                    if (records[i].Screen != key.Key || records[i].Tab != key.Value) continue;
                    items++;
                    if (records[i].Marked) marked++;
                    if (records[i].Blocked) blocked++;
                }
                lines.Add(FormatSummaryLine(label, key.Key, key.Value, items, marked, blocked));
            }
            for (int i = 0; i < records.Count; i++)
                if (records[i].Marked || records[i].Blocked)
                    lines.Add(FormatItemLine(label, records[i]));
            return lines;
        }

        /// <summary>Logs the records of every open stock screen for one capture. Never throws.</summary>
        internal static void LogForCapture(string label)
        {
            List<StockScreenRecord> records;
            try
            {
                records = CollectLive();
            }
            catch (Exception ex)
            {
                ParsekLog.Warn(Tag, LinePrefix + " label=" + label + " collect-failed "
                    + ex.GetType().Name + ": " + ex.Message);
                return;
            }
            foreach (string line in FormatLines(label, records))
                ParsekLog.Info(Tag, line);

            // The stock controls those decisions act on, as stock holds them in this frame:
            // a greyed look is a picture, `interactable` is the fact behind it.
            List<StockScreenControl> controls;
            try
            {
                controls = CollectControlsLive();
            }
            catch (Exception ex)
            {
                ParsekLog.Warn(Tag, ControlPrefix + " label=" + label + " collect-failed "
                    + ex.GetType().Name + ": " + ex.Message);
                return;
            }
            foreach (var c in controls)
                ParsekLog.Info(Tag, FormatControlLine(label, c));
        }

        internal const string ControlPrefix = "control";

        /// <summary>One control line: <c>control label= screen= name= state= interactable= visible=</c>.
        /// A separate verb from <c>record</c>, so a reader of the decision rows never mistakes a
        /// control for a decorated item.</summary>
        internal static string FormatControlLine(string label, StockScreenControl c)
        {
            return ControlPrefix + " label=" + label
                + " screen=" + c.Screen
                + " name=" + c.Name
                + " state=" + (string.IsNullOrEmpty(c.State) ? "-" : c.State)
                + " interactable=" + (c.Interactable ? "true" : "false")
                + " visible=" + (c.Visible ? "true" : "false");
        }

        // ------------------------------------------------------------------ live

        [MethodImpl(MethodImplOptions.NoInlining)]
        internal static List<StockScreenControl> CollectControlsLive()
        {
            var controls = new List<StockScreenControl>();
            if (HighLogic.CurrentGame == null) return controls;

            RDController rd = RDController.Instance;
            if (rd != null && rd.actionButton != null)
                controls.Add(StateButton("RnD", "actionButton:" + TechIdOf(rd.node_selected), rd.actionButton));

            MissionControl mc = MissionControl.Instance;
            if (mc != null)
            {
                string contract = mc.selectedMission != null && mc.selectedMission.contract != null
                    ? MissionControlStockUi.ContractKey(mc.selectedMission.contract) : "-";
                controls.Add(PlainButton("MissionControl", "btnAccept:" + contract, mc.btnAccept));
                controls.Add(PlainButton("MissionControl", "btnDecline:" + contract, mc.btnDecline));
                controls.Add(PlainButton("MissionControl", "btnCancel:" + contract, mc.btnCancel));
            }

            Administration admin = Administration.Instance;
            if (admin != null && admin.btnAcceptCancel != null)
            {
                string selected = admin.SelectedWrapper != null && admin.SelectedWrapper.strategy != null
                    && admin.SelectedWrapper.strategy.Config != null
                    ? admin.SelectedWrapper.strategy.Config.Name : "-";
                controls.Add(StateButton("Administration", "btnAcceptCancel:" + selected, admin.btnAcceptCancel));
            }

            foreach (KSCFacilityContextMenu menu in Object.FindObjectsOfType<KSCFacilityContextMenu>())
                controls.Add(PlainButton("FacilityMenu", "Upgrade:" + StockUiFacilityDecoration.FacilityIdOf(menu),
                    StockUiFacilityDecoration.UpgradeButtonOf(menu)));

            var tooltip = PartListTooltipMasterController.Instance != null
                ? PartListTooltipMasterController.Instance.currentTooltip : null;
            if (tooltip != null)
            {
                controls.Add(PlainButton("PartTooltip", "buttonPurchase", tooltip.buttonPurchase));
                controls.Add(PlainButton("PartTooltip", "buttonPurchaseRed", tooltip.buttonPurchaseRed));
            }

            AstronautComplex complex = Object.FindObjectOfType<AstronautComplex>();
            if (complex != null)
                AddRowButtons(controls, "AstronautComplex", complex);
            CrewAssignmentDialog dialog = CrewAssignmentDialog.Instance;
            if (dialog != null && dialog.isActiveAndEnabled)
                AddRowButtons(controls, "CrewAssignment", dialog);
            return controls;
        }

        private static void AddRowButtons(List<StockScreenControl> controls, string screen, Component scope)
        {
            var seen = new HashSet<string>(StringComparer.Ordinal);
            foreach (CrewListItem row in scope.GetComponentsInChildren<CrewListItem>(true))
            {
                string name = StockUiAstronautDecoration.SafeName(row);
                if (string.IsNullOrEmpty(name) || !seen.Add(name)) continue;
                var c = StateButton(screen, "row:" + name, row.button);
                c.State = (c.State ?? "-") + "/mouseover=" + (row.MouseoverEnabled ? "on" : "off");
                controls.Add(c);
            }
        }

        private static string TechIdOf(RDNode node)
        {
            return node != null && node.tech != null && !string.IsNullOrEmpty(node.tech.techID) ? node.tech.techID : "-";
        }

        private static StockScreenControl PlainButton(string screen, string name, UnityEngine.UI.Button button)
        {
            return new StockScreenControl
            {
                Screen = screen,
                Name = name,
                State = null,
                Interactable = button != null && button.interactable,
                Visible = button != null && button.gameObject.activeInHierarchy,
            };
        }

        private static StockScreenControl StateButton(string screen, string name, UIStateButton button)
        {
            return new StockScreenControl
            {
                Screen = screen,
                Name = name,
                State = button != null ? button.currentState : null,
                Interactable = button != null && button.Button != null && button.Button.interactable,
                Visible = button != null && button.gameObject.activeInHierarchy,
            };
        }

        [MethodImpl(MethodImplOptions.NoInlining)]
        internal static List<StockScreenRecord> CollectLive()
        {
            var records = new List<StockScreenRecord>();
            if (HighLogic.CurrentGame == null) return records;
            CollectRnD(records);
            CollectAstronautComplex(records);
            CollectMissionControl(records);
            CollectAdministration(records);
            CollectCrewDialog(records);
            CollectFacilityMenus(records);
            CollectPartTooltip(records);
            return records;
        }

        private static void CollectRnD(List<StockScreenRecord> records)
        {
            RDController rd = RDController.Instance;
            if (rd == null || rd.nodes == null) return;
            var ids = new List<string>();
            for (int i = 0; i < rd.nodes.Count; i++)
            {
                RDNode node = rd.nodes[i];
                if (node != null && node.tech != null && !string.IsNullOrEmpty(node.tech.techID))
                    ids.Add(node.tech.techID);
            }
            var snapshot = StockUiLiveSnapshot.Current;
            foreach (var d in StockUiDecorationQuery.ForRnD(snapshot.Index, snapshot.UT, ids,
                         ReservationExplanation.DefaultDateFormatter))
                records.Add(StockScreenRecord.From(d));
        }

        private static readonly string[] AstronautTabs =
        {
            StockUiDecorationQuery.AstronautApplicantsTab,
            StockUiDecorationQuery.AstronautAvailableTab,
            StockUiDecorationQuery.AstronautAssignedTab,
            StockUiDecorationQuery.AstronautKiaTab,
        };

        private static void CollectAstronautComplex(List<StockScreenRecord> records)
        {
            AstronautComplex complex = Object.FindObjectOfType<AstronautComplex>();
            if (complex == null) return;
            var snapshot = StockUiLiveSnapshot.Current;
            foreach (string tab in AstronautTabs)
            {
                UIList list = StockUiAstronautDecoration.ListFor(complex, tab);
                if (list == null) continue;
                foreach (CrewListItem row in RowsOf(list))
                {
                    string name = StockUiAstronautDecoration.SafeName(row);
                    if (string.IsNullOrEmpty(name)) continue;
                    records.Add(StockScreenRecord.From(snapshot.Kerbal(name, tab)));
                }
            }
        }

        /// <summary>A UIList's rows through the list's own item accessor: stock parents a
        /// scroll list's items under its content transform, not under the UIList itself.</summary>
        private static List<CrewListItem> RowsOf(UIList list)
        {
            var rows = new List<CrewListItem>();
            for (int i = 0; i < 200; i++)
            {
                UIListItem item;
                try
                {
                    item = list.GetUilistItemAt(i);
                }
                catch (Exception)
                {
                    break;
                }
                if (item == null) break;
                CrewListItem row = item.GetComponent<CrewListItem>();
                if (row != null) rows.Add(row);
            }
            return rows;
        }

        private static void CollectMissionControl(List<StockScreenRecord> records)
        {
            MissionControl mc = MissionControl.Instance;
            if (mc == null) return;
            foreach (MCListItem row in mc.GetComponentsInChildren<MCListItem>(true))
            {
                Contract contract = MissionControlStockUi.ExtractRowContract(row);
                if (contract == null) continue;
                records.Add(StockScreenRecord.From(MissionControlStockUi.DecideNow(contract)));
            }
        }

        private static void CollectAdministration(List<StockScreenRecord> records)
        {
            if (Administration.Instance == null) return;
            var system = Strategies.StrategySystem.Instance;
            if (system == null || system.Strategies == null) return;
            for (int i = 0; i < system.Strategies.Count; i++)
            {
                var s = system.Strategies[i];
                string id = s?.Config?.Name;
                if (string.IsNullOrEmpty(id)) continue;
                ReservationText text;
                bool blocked = s.IsActive
                    ? Patches.StrategyReservationGate.TryRefuseDeactivation(id, out text)
                    : Patches.StrategyReservationGate.TryRefuseActivation(id, out text);
                records.Add(new StockScreenRecord
                {
                    Screen = "Administration",
                    Tab = s.IsActive ? "Active" : "Strategies",
                    Id = id,
                    Kind = blocked ? (s.IsActive ? "StrategyDeactivation" : "StrategyActivation") : "None",
                    Marked = blocked,
                    Blocked = blocked,
                    Why = blocked ? text.Body : null,
                });
            }
        }

        private static void CollectCrewDialog(List<StockScreenRecord> records)
        {
            CrewAssignmentDialog dialog = CrewAssignmentDialog.Instance;
            if (dialog == null || !dialog.isActiveAndEnabled) return;
            var seen = new HashSet<string>(StringComparer.Ordinal);
            foreach (CrewListItem row in dialog.GetComponentsInChildren<CrewListItem>(false))
            {
                string name = StockUiAstronautDecoration.SafeName(row);
                if (string.IsNullOrEmpty(name) || !seen.Add(name)) continue;
                var r = StockScreenRecord.From(StockUiCrewDialogDecoration.DescribeCurrent(name));
                r.Screen = StockUiScreen.CrewAssignment.ToString();
                r.Id = name;
                r.Tab = string.IsNullOrEmpty(r.Tab) ? StockUiDecorationQuery.CrewAssignmentAvailableTab : r.Tab;
                records.Add(r);
            }
        }

        private static void CollectFacilityMenus(List<StockScreenRecord> records)
        {
            var index = CommittedFutureIndexCache.Current;
            double now = CommittedFutureIndexCache.CurrentUT();
            foreach (KSCFacilityContextMenu menu in Object.FindObjectsOfType<KSCFacilityContextMenu>())
            {
                string id = StockUiFacilityDecoration.FacilityIdOf(menu);
                var d = StockUiDecorationQuery.ForFacilityMenu(index, now, id,
                    GameStateRecorder.IsReplayingActions, ReservationExplanation.DefaultDateFormatter);
                records.Add(StockScreenRecord.From(d));
            }
        }

        private static FieldInfo tooltipPartInfoField;

        private static void CollectPartTooltip(List<StockScreenRecord> records)
        {
            var controller = UIMasterController.Instance != null
                ? UIMasterController.Instance.CurrentTooltip as PartListTooltipController
                : null;
            if (controller == null) return;
            if (tooltipPartInfoField == null)
                tooltipPartInfoField = typeof(PartListTooltipController).GetField("partInfo",
                    BindingFlags.Instance | BindingFlags.NonPublic);
            AvailablePart ap = tooltipPartInfoField != null
                ? tooltipPartInfoField.GetValue(controller) as AvailablePart
                : null;
            if (ap == null) return;
            var d = StockUiPartPurchase.DecideLive(ap);
            records.Add(new StockScreenRecord
            {
                Screen = "PartTooltip",
                Tab = HighLogic.LoadedScene == GameScenes.EDITOR ? "Editor" : "RnD",
                Id = ap.name,
                Kind = d.Blocked ? "PartPurchase" : "None",
                Marked = d.Blocked,
                Blocked = d.Blocked,
                Why = d.Why,
            });
        }
    }
}
