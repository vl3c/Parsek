using System;
using System.Collections;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Reflection;
using Contracts;
using HarmonyLib;
using KSP.UI;
using KSP.UI.Screens;
using KSP.UI.Screens.Editor;
using KSP.UI.TooltipTypes;
using UnityEngine;
using Object = UnityEngine.Object;

namespace Parsek.TestCommands
{
    /// <summary>
    /// The thin Unity applier for the two-phase <c>StockScreen</c> verb. Every decision it
    /// makes is in the pure <see cref="TestCommandStockScreen"/>; this file only reaches the
    /// stock screens through their own entry points and polls their readiness.
    ///
    /// <para>NOTHING HERE PRESSES A STOCK ACTION. Opening a building, selecting a row
    /// through its own radio button and spawning a stock tooltip change no career state;
    /// no accept, decline, cancel, hire, dismiss, research, purchase, upgrade or strategy
    /// button is ever invoked. The one save it writes is the one the VAB building itself
    /// writes before it loads the editor.</para>
    ///
    /// <para>A HOVER MOVES THE OPERATOR'S CURSOR. Stock uGUI tooltips follow the Unity
    /// EventSystem, which reads <c>Input.mousePosition</c>, and that tracks the OS cursor
    /// (measured by the <c>op=pointer</c> probe), so a hover moves the real cursor onto the
    /// control and then spawns the control's own tooltip controller through
    /// <c>UIMasterController.SpawnTooltip</c> when the pointer alone did not. Each move writes
    /// the same operator Info line <c>op=pointer</c> writes.</para>
    /// </summary>
    public partial class ParsekTestCommandAddon
    {
        private sealed class StockScreenPending
        {
            internal StockScreenRequest Request;
            internal int StartFrame;
            internal Func<bool> Ready;
            /// <summary>Runs once the first time <see cref="Ready"/> holds; when it returns a
            /// new readiness signal the settle starts over on it (a pane switch after the
            /// screen opened, a direct tooltip spawn after the pointer move).</summary>
            internal Func<Func<bool>> OnReady;
            internal string DetailKey;
            internal Func<string> Detail;
        }

        private StockScreenPending stockScreenPending;

        /// <summary>True while a hover of this verb has moved the OS cursor onto a stock
        /// control; the next non-hover call parks it again.</summary>
        private bool stockScreenPointerMoved;

        private void StockScreenImpl(ParsedCommand cmd)
        {
            string scene = HighLogic.LoadedScene.ToString();
            if (!TestCommandStockScreen.TryParse(
                    ArgOrNull(cmd, TestCommandStockScreen.ScreenArg),
                    ArgOrNull(cmd, TestCommandStockScreen.ActArg),
                    ArgOrNull(cmd, TestCommandStockScreen.ItemArg),
                    ArgOrNull(cmd, TestCommandStockScreen.PartArg),
                    ArgOrNull(cmd, TestCommandStockScreen.PaneArg),
                    out StockScreenRequest request, out string reject, out string detail))
            {
                ParsekLog.Warn(Tag, $"stockscreen rejected reason={reject} {detail}");
                SetExecResult("REJECTED", null, reject + " " + detail);
                return;
            }

            ParsekLog.Info(Tag, TestCommandStockScreen.FormatStartLine(request, scene));
            if (!TestCommandStockScreen.IsValidScene(request, MapScene(HighLogic.LoadedScene)))
            {
                ParsekLog.Warn(Tag, $"stockscreen rejected reason={TestCommandStockScreen.WrongSceneReason} scene={scene}");
                SetExecResult("REJECTED", null, TestCommandStockScreen.WrongSceneReason + " scene=" + scene);
                return;
            }
            if (HighLogic.CurrentGame == null || HighLogic.CurrentGame.Mode != Game.Modes.CAREER)
            {
                string mode = HighLogic.CurrentGame != null ? HighLogic.CurrentGame.Mode.ToString() : "none";
                ParsekLog.Warn(Tag, $"stockscreen rejected reason={TestCommandStockScreen.CareerOnlyReason} mode={mode}");
                SetExecResult("REJECTED", null, TestCommandStockScreen.CareerOnlyReason + " mode=" + mode);
                return;
            }

            var pending = new StockScreenPending { Request = request, StartFrame = Time.frameCount };
            string failure = null;
            try
            {
                // Every act but a hover starts from a frame with no stock tooltip and the
                // cursor off the screen's controls, so a capture after an open or a select
                // shows the screen and not the previous hover.
                if (request.Act != StockScreenAct.Hover)
                    ResetStockHover();
                failure = StartStockScreenAct(request, pending);
            }
            catch (Exception ex)
            {
                failure = TestCommandStockScreen.OpenFailedReason + " " + ex.GetType().Name + ": " + ex.Message;
            }
            if (failure != null)
            {
                ParsekLog.Warn(Tag, $"stockscreen refused {failure}");
                string verdict = failure.StartsWith(TestCommandStockScreen.OpenFailedReason, StringComparison.Ordinal)
                    ? "ERROR" : "REJECTED";
                SetExecResult(verdict, null, failure);
                return;
            }
            stockScreenPending = pending;
            SetExecResult(PendingVerdict, null, null);
        }

        /// <summary>Performs the act and arms the readiness signal; returns a refusal or null.</summary>
        private string StartStockScreenAct(StockScreenRequest r, StockScreenPending p)
        {
            switch (r.Screen)
            {
                case StockScreenKind.RnD: return StartRnD(r, p);
                case StockScreenKind.Astronaut: return StartAstronaut(r, p);
                case StockScreenKind.MissionControl: return StartMissionControl(r, p);
                case StockScreenKind.Administration: return StartAdministration(r, p);
                case StockScreenKind.FacilityMenu: return StartFacilityMenu(r, p);
                case StockScreenKind.LaunchSite: return StartLaunchSite(r, p);
                case StockScreenKind.Editor: return StartEditor(r, p);
                case StockScreenKind.CrewDialog: return StartCrewDialog(r, p);
                default: return TestCommandStockScreen.ActUnsupportedReason;
            }
        }

        // ------------------------------------------------------------------ R&D

        private string StartRnD(StockScreenRequest r, StockScreenPending p)
        {
            RDController rd = RDController.Instance ?? Object.FindObjectOfType<RDController>();
            switch (r.Act)
            {
                case StockScreenAct.Open:
                    if (rd != null) return TestCommandStockScreen.AlreadyOpenReason + " screen=rnd";
                    if (!EnterBuilding<RnDBuilding>()) return TestCommandStockScreen.EntryNotFoundReason + " building=RnDBuilding";
                    p.Ready = () =>
                    {
                        var c = RDController.Instance;
                        return c != null && c.nodes != null && c.nodes.Count > 0;
                    };
                    p.DetailKey = "nodes";
                    p.Detail = () => Int(RDController.Instance != null ? RDController.Instance.nodes.Count : 0);
                    return null;
                case StockScreenAct.Close:
                    if (rd == null) return TestCommandStockScreen.NotOpenReason + " screen=rnd";
                    RDController.OnRDTreeDespawn.Fire(rd);
                    GameEvents.onGUIRnDComplexDespawn.Fire();
                    p.Ready = () => RDController.Instance == null && Object.FindObjectOfType<RDController>() == null;
                    return null;
                case StockScreenAct.Select:
                {
                    if (rd == null) return TestCommandStockScreen.NotOpenReason + " screen=rnd";
                    RDNode node = FindRdNode(rd, r.Item);
                    if (node == null) return TestCommandStockScreen.ItemNotFoundReason + " tech=" + r.Item;
                    if (rd.node_selected != node)
                    {
                        // The node's own click body (private RDNode.NodeInput): unselect the
                        // previous node, select this one and show its panel.
                        MethodInfo input = AccessTools.Method(typeof(RDNode), "NodeInput");
                        if (input != null) input.Invoke(node, null);
                        else
                        {
                            if (rd.node_selected != null) rd.node_selected.UnselectNode();
                            rd.node_selected = node;
                            node.SelectNode();
                            rd.ShowNodePanel(node);
                        }
                    }
                    p.Ready = () => RDController.Instance != null && RDController.Instance.node_selected == node;
                    p.DetailKey = "state";
                    p.Detail = () => node.state.ToString();
                    return null;
                }
                case StockScreenAct.Hover:
                {
                    if (rd == null) return TestCommandStockScreen.NotOpenReason + " screen=rnd";
                    if (r.Part != null)
                    {
                        PartListTooltipController partTip = FindRdPartTooltip(rd, r.Part, out int listed);
                        if (partTip == null)
                            return TestCommandStockScreen.ItemNotFoundReason + " part=" + r.Part + " listed=" + Int(listed);
                        return ArmHover(p, partTip, partTip.transform as RectTransform);
                    }
                    RDNode node = FindRdNode(rd, r.Item);
                    if (node == null) return TestCommandStockScreen.ItemNotFoundReason + " tech=" + r.Item;
                    TooltipController tip = node.graphics != null ? node.graphics.tooltip : null;
                    if (tip == null) return TestCommandStockScreen.NoTooltipReason + " tech=" + r.Item;
                    return ArmHover(p, tip, tip.transform as RectTransform);
                }
            }
            return TestCommandStockScreen.ActUnsupportedReason;
        }

        private static RDNode FindRdNode(RDController rd, string techId)
        {
            if (rd == null || rd.nodes == null || string.IsNullOrEmpty(techId)) return null;
            for (int i = 0; i < rd.nodes.Count; i++)
            {
                RDNode n = rd.nodes[i];
                if (n != null && n.tech != null && n.tech.techID == techId) return n;
            }
            return null;
        }

        private static PartListTooltipController FindRdPartTooltip(RDController rd, string partName, out int listed)
        {
            listed = 0;
            if (rd.partList == null || rd.partList.listItems == null) return null;
            foreach (RDPartListItem item in rd.partList.listItems)
            {
                if (item == null) continue;
                listed++;
                if (item.myPart == null || item.myPart.name != partName) continue;
                var tip = item.GetComponent<PartListTooltipController>()
                          ?? item.GetComponentInParent<PartListTooltipController>()
                          ?? item.GetComponentInChildren<PartListTooltipController>(true);
                if (tip != null) return tip;
            }
            return null;
        }

        // ------------------------------------------------------------------ Astronaut Complex

        private string StartAstronaut(StockScreenRequest r, StockScreenPending p)
        {
            AstronautComplex ac = Object.FindObjectOfType<AstronautComplex>();
            switch (r.Act)
            {
                case StockScreenAct.Open:
                    if (ac != null) return TestCommandStockScreen.AlreadyOpenReason + " screen=astronaut";
                    if (HighLogic.LoadedScene == GameScenes.EDITOR)
                    {
                        // The crew panel's own Astronaut Complex button.
                        if (CrewAssignmentDialog.Instance == null)
                            return TestCommandStockScreen.NotOpenReason + " screen=crewdialog (open it first)";
                        CrewAssignmentDialog.Instance.ButtonAstronautComplex();
                    }
                    else if (!EnterBuilding<AstronautComplexFacility>())
                    {
                        return TestCommandStockScreen.EntryNotFoundReason + " building=AstronautComplexFacility";
                    }
                    p.Ready = () =>
                    {
                        var c = Object.FindObjectOfType<AstronautComplex>();
                        return c != null && c.GetComponentsInChildren<CrewListItem>(true).Length > 0;
                    };
                    p.DetailKey = "rows";
                    p.Detail = () =>
                    {
                        var c = Object.FindObjectOfType<AstronautComplex>();
                        return Int(c != null ? c.GetComponentsInChildren<CrewListItem>(true).Length : 0);
                    };
                    return null;
                case StockScreenAct.Close:
                    if (ac == null) return TestCommandStockScreen.NotOpenReason + " screen=astronaut";
                    GameEvents.onGUIAstronautComplexDespawn.Fire();
                    p.Ready = () => Object.FindObjectOfType<AstronautComplex>() == null;
                    return null;
                case StockScreenAct.Hover:
                {
                    if (ac == null) return TestCommandStockScreen.NotOpenReason + " screen=astronaut";
                    CrewListItem row = FindCrewRow(ac, r.Item);
                    if (row == null) return TestCommandStockScreen.ItemNotFoundReason + " kerbal=" + r.Item;
                    TooltipController tip = row.GetComponentInChildren<TooltipController_CrewAC>(true);
                    if (tip == null) return TestCommandStockScreen.NoTooltipReason + " kerbal=" + r.Item;
                    return ArmHover(p, tip, row.transform as RectTransform);
                }
            }
            return TestCommandStockScreen.ActUnsupportedReason;
        }

        private static CrewListItem FindCrewRow(Component scope, string kerbalName)
        {
            if (scope == null || string.IsNullOrEmpty(kerbalName)) return null;
            foreach (CrewListItem row in scope.GetComponentsInChildren<CrewListItem>(true))
                if (string.Equals(StockUiAstronautDecoration.SafeName(row), kerbalName, StringComparison.Ordinal))
                    return row;
            return null;
        }

        // ------------------------------------------------------------------ Mission Control

        private string StartMissionControl(StockScreenRequest r, StockScreenPending p)
        {
            MissionControl mc = MissionControl.Instance;
            switch (r.Act)
            {
                case StockScreenAct.Open:
                    if (mc != null) return TestCommandStockScreen.AlreadyOpenReason + " screen=missioncontrol";
                    if (!EnterBuilding<MissionControlBuilding>())
                        return TestCommandStockScreen.EntryNotFoundReason + " building=MissionControlBuilding";
                    p.Ready = () => MissionControl.Instance != null;
                    if (r.Pane != null)
                    {
                        string pane = r.Pane;
                        p.OnReady = () =>
                        {
                            ApplyMissionControlPane(MissionControl.Instance, pane);
                            return () => MissionControl.Instance != null;
                        };
                    }
                    p.DetailKey = "rows";
                    p.Detail = () => Int(MissionControl.Instance != null
                        ? MissionControl.Instance.GetComponentsInChildren<MCListItem>(true).Length : 0);
                    return null;
                case StockScreenAct.Close:
                    if (mc == null) return TestCommandStockScreen.NotOpenReason + " screen=missioncontrol";
                    GameEvents.onGUIMissionControlDespawn.Fire();
                    p.Ready = () => MissionControl.Instance == null && Object.FindObjectOfType<MissionControl>() == null;
                    return null;
                case StockScreenAct.Select:
                {
                    if (mc == null) return TestCommandStockScreen.NotOpenReason + " screen=missioncontrol";
                    if (r.Pane != null) ApplyMissionControlPane(mc, r.Pane);
                    MCListItem row = FindMissionControlRow(mc, r.Item, out int rows);
                    if (row == null)
                        return TestCommandStockScreen.ItemNotFoundReason + " contract=" + r.Item + " rows=" + Int(rows);
                    // The row's own radio button: fires MissionControl.OnSelectContract, which
                    // shows the detail panel and runs UpdateInfoPanelContract.
                    row.radioButton.SetState(UIRadioButton.State.True, UIRadioButton.CallType.USER, null);
                    string key = r.Item;
                    p.Ready = () =>
                    {
                        var m = MissionControl.Instance;
                        return m != null && m.selectedMission != null && m.selectedMission.contract != null
                               && MissionControlStockUi.ContractKey(m.selectedMission.contract) == key;
                    };
                    p.DetailKey = "state";
                    p.Detail = () =>
                    {
                        var m = MissionControl.Instance;
                        return m != null && m.selectedMission != null && m.selectedMission.contract != null
                            ? m.selectedMission.contract.ContractState.ToString() : "-";
                    };
                    return null;
                }
            }
            return TestCommandStockScreen.ActUnsupportedReason;
        }

        private static void ApplyMissionControlPane(MissionControl mc, string pane)
        {
            if (mc == null) return;
            switch (pane)
            {
                case "available": mc.SetDisplayModeAvailable(); break;
                case "active": mc.SetDisplayModeActive(); break;
                case "archive": mc.SetDisplayModeArchive(); break;
            }
        }

        private static MCListItem FindMissionControlRow(MissionControl mc, string contractKey, out int rows)
        {
            rows = 0;
            foreach (MCListItem row in mc.GetComponentsInChildren<MCListItem>(true))
            {
                rows++;
                Contract c = MissionControlStockUi.ExtractRowContract(row);
                if (c != null && MissionControlStockUi.ContractKey(c) == contractKey) return row;
            }
            return null;
        }

        // ------------------------------------------------------------------ Administration

        private string StartAdministration(StockScreenRequest r, StockScreenPending p)
        {
            Administration admin = Administration.Instance;
            switch (r.Act)
            {
                case StockScreenAct.Open:
                    if (admin != null) return TestCommandStockScreen.AlreadyOpenReason + " screen=administration";
                    if (!EnterBuilding<AdministrationFacility>())
                        return TestCommandStockScreen.EntryNotFoundReason + " building=AdministrationFacility";
                    p.Ready = () => Administration.Instance != null;
                    return null;
                case StockScreenAct.Close:
                    if (admin == null) return TestCommandStockScreen.NotOpenReason + " screen=administration";
                    GameEvents.onGUIAdministrationFacilityDespawn.Fire();
                    p.Ready = () => Administration.Instance == null && Object.FindObjectOfType<Administration>() == null;
                    return null;
                case StockScreenAct.Select:
                {
                    if (admin == null) return TestCommandStockScreen.NotOpenReason + " screen=administration";
                    UIRadioButton button = FindStrategyButton(admin, r.Item, out int buttons);
                    if (button == null)
                        return TestCommandStockScreen.ItemNotFoundReason + " strategy=" + r.Item + " buttons=" + Int(buttons);
                    // The list item's own radio button: fires StrategyWrapper.OnTrue ->
                    // Administration.SetSelectedStrategy.
                    button.SetState(UIRadioButton.State.True, UIRadioButton.CallType.USER, null);
                    string id = r.Item;
                    p.Ready = () =>
                    {
                        var a = Administration.Instance;
                        return a != null && a.SelectedWrapper != null && a.SelectedWrapper.strategy != null
                               && a.SelectedWrapper.strategy.Config != null && a.SelectedWrapper.strategy.Config.Name == id;
                    };
                    p.DetailKey = "active";
                    p.Detail = () =>
                    {
                        var a = Administration.Instance;
                        return a != null && a.SelectedWrapper != null && a.SelectedWrapper.strategy != null
                            ? Bool(a.SelectedWrapper.strategy.IsActive) : "-";
                    };
                    return null;
                }
            }
            return TestCommandStockScreen.ActUnsupportedReason;
        }

        private static UIRadioButton FindStrategyButton(Administration admin, string strategyId, out int buttons)
        {
            buttons = 0;
            UIRadioButton fallback = null;
            foreach (UIRadioButton b in admin.GetComponentsInChildren<UIRadioButton>(true))
            {
                var wrapper = b.Data as Administration.StrategyWrapper;
                if (wrapper == null || wrapper.strategy == null || wrapper.strategy.Config == null) continue;
                buttons++;
                if (wrapper.strategy.Config.Name != strategyId) continue;
                // Prefer the department list's row (the one a player clicks to read the
                // strategy); the active-strategy strip carries the same wrapper type.
                if (b.GetComponentInParent<StrategyListItem>() != null) return b;
                if (fallback == null) fallback = b;
            }
            return fallback;
        }

        // ------------------------------------------------------------------ facility menu

        private string StartFacilityMenu(StockScreenRequest r, StockScreenPending p)
        {
            switch (r.Act)
            {
                case StockScreenAct.Open:
                {
                    if (Object.FindObjectsOfType<KSCFacilityContextMenu>().Length > 0)
                        return TestCommandStockScreen.AlreadyOpenReason + " screen=facilitymenu";
                    SpaceCenterBuilding building = null;
                    foreach (var b in Object.FindObjectsOfType<SpaceCenterBuilding>())
                        if (b != null && b.Facility != null && b.Facility.id == r.Item) { building = b; break; }
                    if (building == null)
                        return TestCommandStockScreen.ItemNotFoundReason + " facility=" + r.Item;
                    // The building's own right-click path (input lock + dismiss callback).
                    building.OnRightClick();
                    string id = r.Item;
                    p.Ready = () =>
                    {
                        KSCFacilityContextMenu m = FindOpenFacilityMenu(id);
                        return m != null && StockUiFacilityDecoration.UpgradeButtonOf(m) != null;
                    };
                    p.DetailKey = "upgradeInteractable";
                    p.Detail = () =>
                    {
                        KSCFacilityContextMenu m = FindOpenFacilityMenu(id);
                        var button = m != null ? StockUiFacilityDecoration.UpgradeButtonOf(m) : null;
                        return button != null ? Bool(button.interactable) : "-";
                    };
                    return null;
                }
                case StockScreenAct.Hover:
                {
                    KSCFacilityContextMenu menu = FindOpenFacilityMenu(null);
                    if (menu == null) return TestCommandStockScreen.NotOpenReason + " screen=facilitymenu";
                    var button = StockUiFacilityDecoration.UpgradeButtonOf(menu);
                    TooltipController tip = button != null ? button.GetComponent<TooltipController>() : null;
                    if (tip == null) return TestCommandStockScreen.NoTooltipReason + " control=Upgrade";
                    return ArmHover(p, tip, button.transform as RectTransform);
                }
                case StockScreenAct.Close:
                {
                    KSCFacilityContextMenu menu = FindOpenFacilityMenu(null);
                    if (menu == null) return TestCommandStockScreen.NotOpenReason + " screen=facilitymenu";
                    menu.Dismiss(KSCFacilityContextMenu.DismissAction.None);
                    p.Ready = () => Object.FindObjectsOfType<KSCFacilityContextMenu>().Length == 0;
                    return null;
                }
            }
            return TestCommandStockScreen.ActUnsupportedReason;
        }

        private static KSCFacilityContextMenu FindOpenFacilityMenu(string facilityId)
        {
            foreach (var m in Object.FindObjectsOfType<KSCFacilityContextMenu>())
                if (m != null && (facilityId == null || StockUiFacilityDecoration.FacilityIdOf(m) == facilityId))
                    return m;
            return null;
        }

        // ------------------------------------------------------------------ launch site

        private string StartLaunchSite(StockScreenRequest r, StockScreenPending p)
        {
            VesselSpawnDialog dialog = VesselSpawnDialog.Instance;
            bool open = dialog != null && dialog.Visible;
            switch (r.Act)
            {
                case StockScreenAct.Open:
                {
                    if (open) return TestCommandStockScreen.AlreadyOpenReason + " screen=launchsite";
                    LaunchSiteFacility pad = null;
                    foreach (var f in Object.FindObjectsOfType<LaunchSiteFacility>())
                        if (f != null && f.facilityType == EditorFacility.VAB) { pad = f; break; }
                    if (pad == null) return TestCommandStockScreen.EntryNotFoundReason + " building=LaunchSiteFacility(VAB)";
                    pad.EnterBuilding();
                    p.Ready = () => VesselSpawnDialog.Instance != null && VesselSpawnDialog.Instance.Visible;
                    p.DetailKey = "craft";
                    p.Detail = () => Int(VesselDataItems(VesselSpawnDialog.Instance)?.Count ?? 0);
                    return null;
                }
                case StockScreenAct.Select:
                {
                    if (!open) return TestCommandStockScreen.NotOpenReason + " screen=launchsite";
                    IList items = VesselDataItems(dialog);
                    object match = null;
                    int count = items != null ? items.Count : 0;
                    for (int i = 0; i < count; i++)
                    {
                        object item = items[i];
                        FieldInfo nameField = item != null ? item.GetType().GetField("name") : null;
                        if (nameField != null && (nameField.GetValue(item) as string) == r.Item) { match = item; break; }
                    }
                    if (match == null) return TestCommandStockScreen.ItemNotFoundReason + " craft=" + r.Item + " listed=" + Int(count);
                    // The list's own selection body (private SelectVesselDataItem): selects the
                    // row and runs UpdateVesselInfoPanel, which fills the crew list.
                    MethodInfo select = AccessTools.Method(typeof(VesselSpawnDialog), "SelectVesselDataItem");
                    if (select == null) return TestCommandStockScreen.OpenFailedReason + " SelectVesselDataItem not found";
                    select.Invoke(dialog, new[] { match });
                    p.Ready = () => CrewAssignmentDialog.Instance != null
                                    && CrewAssignmentDialog.Instance.GetComponentsInChildren<CrewListItem>(true).Length > 0;
                    p.DetailKey = "crewRows";
                    p.Detail = () => Int(CrewAssignmentDialog.Instance != null
                        ? CrewAssignmentDialog.Instance.GetComponentsInChildren<CrewListItem>(true).Length : 0);
                    return null;
                }
                case StockScreenAct.Hover:
                {
                    if (!open) return TestCommandStockScreen.NotOpenReason + " screen=launchsite";
                    return HoverCrewDialogRow(r, p, dialog);
                }
                case StockScreenAct.Close:
                {
                    if (!open) return TestCommandStockScreen.NotOpenReason + " screen=launchsite";
                    // The dialog's own Close button.
                    FieldInfo closeField = AccessTools.Field(typeof(VesselSpawnDialog), "buttonClose");
                    var close = closeField != null ? closeField.GetValue(dialog) as UnityEngine.UI.Button : null;
                    if (close == null) return TestCommandStockScreen.OpenFailedReason + " buttonClose not found";
                    close.onClick.Invoke();
                    p.Ready = () => VesselSpawnDialog.Instance == null || !VesselSpawnDialog.Instance.Visible;
                    return null;
                }
            }
            return TestCommandStockScreen.ActUnsupportedReason;
        }

        private static IList VesselDataItems(VesselSpawnDialog dialog)
        {
            if (dialog == null) return null;
            FieldInfo f = AccessTools.Field(typeof(VesselSpawnDialog), "vesselDataItemList");
            return f != null ? f.GetValue(dialog) as IList : null;
        }

        private string HoverCrewDialogRow(StockScreenRequest r, StockScreenPending p, Component scope)
        {
            CrewListItem row = FindCrewRow(scope, r.Item);
            if (row == null) return TestCommandStockScreen.ItemNotFoundReason + " kerbal=" + r.Item;
            TooltipController tip = row.GetComponentInChildren<TooltipController_CrewAC>(true);
            if (tip == null) return TestCommandStockScreen.NoTooltipReason + " kerbal=" + r.Item;
            return ArmHover(p, tip, row.transform as RectTransform);
        }

        // ------------------------------------------------------------------ editor

        private string StartEditor(StockScreenRequest r, StockScreenPending p)
        {
            switch (r.Act)
            {
                case StockScreenAct.Open:
                {
                    string path = Path.Combine(Path.Combine(Path.Combine(Path.Combine(
                        KSPUtil.ApplicationRootPath, "saves"), HighLogic.SaveFolder), "Ships"), "VAB");
                    path = Path.Combine(path, r.Item + ".craft");
                    if (!File.Exists(path)) return TestCommandStockScreen.ItemNotFoundReason + " craft=" + r.Item;
                    // What the VAB building does before it loads the editor.
                    GamePersistence.SaveGame("persistent", HighLogic.SaveFolder, SaveMode.OVERWRITE);
                    EditorDriver.StartAndLoadVessel(path, EditorFacility.VAB);
                    p.Ready = () => HighLogic.LoadedScene == GameScenes.EDITOR
                                    && EditorLogic.fetch != null && EditorLogic.fetch.ship != null
                                    && EditorLogic.fetch.ship.parts.Count > 0
                                    && EditorPartList.Instance != null
                                    && !EditorPanelsTransitioning();
                    p.DetailKey = "parts";
                    p.Detail = () => Int(EditorLogic.fetch != null && EditorLogic.fetch.ship != null
                        ? EditorLogic.fetch.ship.parts.Count : 0);
                    return null;
                }
                case StockScreenAct.Close:
                {
                    if (EditorLogic.fetch == null) return TestCommandStockScreen.NotOpenReason + " screen=editor";
                    // The editor's own exit body past the "leave?" prompt (private onExitConfirm):
                    // backs up the ship, saves and loads the Space Center.
                    MethodInfo exit = AccessTools.Method(typeof(EditorLogic), "onExitConfirm");
                    if (exit == null) return TestCommandStockScreen.OpenFailedReason + " EditorLogic.onExitConfirm not found";
                    exit.Invoke(EditorLogic.fetch, null);
                    p.Ready = () => HighLogic.LoadedScene == GameScenes.SPACECENTER && HighLogic.CurrentGame != null;
                    return null;
                }
                case StockScreenAct.Hover:
                {
                    PartListTooltipController tip = FindEditorPartTooltip(r.Part, out int icons);
                    if (tip == null) return TestCommandStockScreen.ItemNotFoundReason + " part=" + r.Part + " icons=" + Int(icons);
                    return ArmHover(p, tip, tip.transform as RectTransform);
                }
            }
            return TestCommandStockScreen.ActUnsupportedReason;
        }

        /// <summary>True while any VAB/SPH side panel is still sliding.</summary>
        private static bool EditorPanelsTransitioning()
        {
            var panels = EditorPanels.Instance;
            if (panels == null) return false;
            return (panels.partsEditor != null && panels.partsEditor.Transitioning)
                   || (panels.crew != null && panels.crew.Transitioning)
                   || (panels.actions != null && panels.actions.Transitioning)
                   || (panels.cargo != null && panels.cargo.Transitioning);
        }

        private static PartListTooltipController FindEditorPartTooltip(string partName, out int icons)
        {
            icons = 0;
            foreach (EditorPartIcon icon in Object.FindObjectsOfType<EditorPartIcon>())
            {
                if (icon == null || icon is RDPartListItem) continue;
                icons++;
                if (icon.partInfo == null || icon.partInfo.name != partName) continue;
                var tip = icon.GetComponent<PartListTooltipController>();
                if (tip != null) return tip;
            }
            return null;
        }

        private string StartCrewDialog(StockScreenRequest r, StockScreenPending p)
        {
            switch (r.Act)
            {
                case StockScreenAct.Open:
                    if (EditorLogic.fetch == null) return TestCommandStockScreen.NotOpenReason + " screen=editor";
                    EditorLogic.fetch.SelectPanelCrew();
                    // The panel slides in over about a second; a capture mid-slide photographs
                    // an empty column and a hover lands off-screen.
                    p.Ready = () => CrewAssignmentDialog.Instance != null
                                    && CrewAssignmentDialog.Instance.isActiveAndEnabled
                                    && CrewAssignmentDialog.Instance.GetComponentsInChildren<CrewListItem>(false).Length > 0
                                    && !EditorPanelsTransitioning();
                    p.DetailKey = "crewRows";
                    p.Detail = () => Int(CrewAssignmentDialog.Instance != null
                        ? CrewAssignmentDialog.Instance.GetComponentsInChildren<CrewListItem>(false).Length : 0);
                    return null;
                case StockScreenAct.Hover:
                    if (CrewAssignmentDialog.Instance == null) return TestCommandStockScreen.NotOpenReason + " screen=crewdialog";
                    return HoverCrewDialogRow(r, p, CrewAssignmentDialog.Instance);
            }
            return TestCommandStockScreen.ActUnsupportedReason;
        }

        // ------------------------------------------------------------------ shared

        private static bool EnterBuilding<T>() where T : SpaceCenterBuilding
        {
            T building = Object.FindObjectOfType<T>();
            if (building == null) return false;
            building.EnterBuilding();
            return true;
        }

        /// <summary>
        /// Moves the OS cursor onto <paramref name="target"/> and arms the tooltip settle: once
        /// a frame has passed, a tooltip the pointer did not spawn is spawned through stock's
        /// own <c>UIMasterController.SpawnTooltip</c>, and the call is ready when that
        /// controller is stock's current tooltip.
        /// </summary>
        private string ArmHover(StockScreenPending p, TooltipController tip, RectTransform target)
        {
            string pointer = MovePointerOnto(target);
            string how = "pointer";
            p.Ready = () => true;
            p.OnReady = () =>
            {
                var master = UIMasterController.Instance;
                if (master != null && !ReferenceEquals(master.CurrentTooltip, tip) && tip != null)
                {
                    how = "direct";
                    master.SpawnTooltip(tip);
                }
                return () => UIMasterController.Instance != null
                             && ReferenceEquals(UIMasterController.Instance.CurrentTooltip, tip);
            };
            p.DetailKey = "tooltip";
            p.Detail = () => tip.GetType().Name + "/" + how + "/" + pointer;
            return null;
        }

        /// <summary>Despawns stock's current tooltip and parks the cursor a hover moved at the
        /// client corner (<c>op=pointer park=true</c>'s point).</summary>
        private void ResetStockHover()
        {
            var master = UIMasterController.Instance;
            if (master != null && master.CurrentTooltip != null)
                master.DestroyCurrentTooltip();
            if (!stockScreenPointerMoved) return;
            stockScreenPointerMoved = false;
            int x = (int)TestCommandUiPointer.ParkX;
            int y = (int)TestCommandUiPointer.ParkY;
            if (!IsWindowsRuntime()
                || !TryResolveDesktopPoint(x, y, out int sx, out int sy, out string via, out IntPtr _))
                return;
            ParsekLog.Info(Tag, $"stockscreen pointer moving the OS cursor to client {Int(x)},{Int(y)} "
                + $"(desktop {Int(sx)},{Int(sy)} via={via}) to park it after a hover. This is machine-wide");
            try
            {
                SetCursorPos(sx, sy);
            }
            catch (Exception)
            {
                // A platform the pointer cannot move on; the despawned tooltip is what matters.
            }
        }

        /// <summary>Moves the real OS cursor to the centre of a uGUI rect; returns a token
        /// saying whether it did (the tooltip is still spawned directly when it did not).</summary>
        private string MovePointerOnto(RectTransform target)
        {
            if (target == null) return "no-rect";
            if (!IsWindowsRuntime()) return "unsupported-platform";
            var corners = new Vector3[4];
            target.GetWorldCorners(corners);
            Vector3 center = (corners[0] + corners[2]) * 0.5f;
            Canvas canvas = target.GetComponentInParent<Canvas>();
            Camera cam = null;
            if (canvas != null && canvas.renderMode != RenderMode.ScreenSpaceOverlay)
                cam = canvas.worldCamera ?? (UIMasterController.Instance != null ? UIMasterController.Instance.uiCamera : null);
            Vector2 sp = RectTransformUtility.WorldToScreenPoint(cam, center);
            int clientX = Mathf.RoundToInt(sp.x);
            int clientY = Screen.height - Mathf.RoundToInt(sp.y);
            if (!TestCommandUiPointer.IsInsideClient(clientX, clientY, Screen.width, Screen.height))
                return "off-screen";
            if (!TryResolveDesktopPoint(clientX, clientY, out int sx, out int sy, out string via, out IntPtr _))
                return "window-unresolved";
            ParsekLog.Info(Tag, $"stockscreen pointer moving the OS cursor to client {Int(clientX)},{Int(clientY)} "
                + $"(desktop {Int(sx)},{Int(sy)} via={via}). This is machine-wide: an operator using the "
                + "mouse during the run will see it jump, and his own next move invalidates the hover");
            try
            {
                bool moved = SetCursorPos(sx, sy);
                if (moved) stockScreenPointerMoved = true;
                return moved ? "moved" : "os-refused";
            }
            catch (Exception ex)
            {
                return "threw-" + ex.GetType().Name;
            }
        }

        private void TryCompleteStockScreen(double now)
        {
            StockScreenPending p = stockScreenPending;
            double elapsed = now - completionStartedAt;
            double budget = DeferralBudget.BudgetSeconds(TestCommandStockScreen.Verb);
            bool expired = DeferralBudget.ShouldTimeout(completionStartedAt, now, budget);
            if (p == null)
            {
                FinishStockScreen(null, "ERROR", TestCommandStockScreen.NotSettledReason + " no-pending-state", 0);
                return;
            }

            bool ready;
            try
            {
                ready = p.Ready == null || p.Ready();
            }
            catch (Exception)
            {
                ready = false;
            }
            int frames = Time.frameCount - p.StartFrame;
            var outcome = TestCommandStockScreen.DecidePoll(ready, frames, expired);
            if (outcome == StockScreenPollOutcome.NotYet) return;
            if (outcome == StockScreenPollOutcome.Ready && p.OnReady != null)
            {
                Func<Func<bool>> onReady = p.OnReady;
                p.OnReady = null;
                Func<bool> next = null;
                try
                {
                    next = onReady();
                }
                catch (Exception ex)
                {
                    FinishStockScreen(p, "ERROR", TestCommandStockScreen.OpenFailedReason + " " + ex.GetType().Name + ": " + ex.Message, frames);
                    return;
                }
                if (next != null)
                {
                    p.Ready = next;
                    p.StartFrame = Time.frameCount;
                    return;
                }
            }

            if (outcome == StockScreenPollOutcome.TimedOut)
            {
                FinishStockScreen(p, "ERROR", TestCommandStockScreen.NotSettledReason
                    + " elapsed=" + elapsed.ToString("F1", CultureInfo.InvariantCulture) + "s", frames);
                return;
            }
            FinishStockScreen(p, "OK", null, frames);
        }

        private void FinishStockScreen(StockScreenPending p, string verdict, string msg, int frames)
        {
            string id = completionId; long seq = completionSeq; string verb = completionVerb;
            stockScreenPending = null;
            ClearTwoPhase();
            string scene = HighLogic.LoadedScene.ToString();
            if (verdict != "OK" || p == null)
            {
                ParsekLog.Error(Tag, $"stockscreen error {msg} scene={scene}");
                EmitExecutedTerminal(id, seq, verb, "ERROR", null, msg, dequeueHead: true);
                return;
            }
            string detail = null;
            try
            {
                detail = p.Detail != null ? p.Detail() : null;
            }
            catch (Exception ex)
            {
                detail = "detail-threw-" + ex.GetType().Name;
            }
            ParsekLog.Info(Tag, TestCommandStockScreen.FormatOkLine(p.Request, scene, p.DetailKey, detail, frames));
            EmitExecutedTerminal(id, seq, verb, "OK",
                TestCommandStockScreen.BuildPayload(p.Request, scene, p.DetailKey, detail, frames),
                null, dequeueHead: true);
        }
    }
}
