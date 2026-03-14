using ClickThroughFix;
using KSP.UI.Screens;
using ToolbarControl_NS;
using UnityEngine;

namespace Parsek
{
    /// <summary>
    /// KSC scene host for the Parsek UI. Provides recording management,
    /// resource budget viewing, and game actions in the Space Center scene.
    /// Reuses ParsekUI in KSC mode (flight-only controls hidden).
    /// </summary>
    [KSPAddon(KSPAddon.Startup.SpaceCentre, false)]
    public class ParsekKSC : MonoBehaviour
    {
        private ToolbarControl toolbarControl;
        private ParsekUI ui;
        private bool showUI;
        private Rect windowRect = new Rect(20, 100, 200, 10);

        void Start()
        {
            ParsekLog.Info("KSC", "ParsekKSC starting in Space Center scene");

            ui = new ParsekUI(ParsekUI.UIMode.KSC);

            toolbarControl = gameObject.AddComponent<ToolbarControl>();
            toolbarControl.AddToAllToolbars(
                () => { showUI = true; ParsekLog.Verbose("KSC", "Toolbar button ON"); },
                () => { showUI = false; ParsekLog.Verbose("KSC", "Toolbar button OFF"); },
                ApplicationLauncher.AppScenes.SPACECENTER,
                ParsekFlight.MODID, "parsekKSCButton",
                "Parsek/Textures/parsek_38",
                "Parsek/Textures/parsek_24",
                ParsekFlight.MODNAME
            );

            ParsekLog.Info("KSC", "ParsekKSC initialized");
        }

        void OnGUI()
        {
            if (!showUI) return;

            windowRect = ClickThruBlocker.GUILayoutWindow(
                GetInstanceID(), windowRect, ui.DrawWindow,
                "Parsek", GUILayout.Width(250));

            ui.DrawRecordingsWindowIfOpen(windowRect);
            ui.DrawActionsWindowIfOpen(windowRect);
            ui.DrawSettingsWindowIfOpen(windowRect);
        }

        void OnDestroy()
        {
            ParsekLog.Info("KSC", "ParsekKSC destroyed");
            ui?.Cleanup();
            if (toolbarControl != null)
            {
                toolbarControl.OnDestroy();
                Destroy(toolbarControl);
            }
        }
    }
}
