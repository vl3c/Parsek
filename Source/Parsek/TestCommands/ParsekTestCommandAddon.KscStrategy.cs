using System;
using UnityEngine;

namespace Parsek.TestCommands
{
    /// <summary>
    /// M-A2 partial: hosts stock's Administration screen, hidden, for the one
    /// <c>KscAction action=activate-strategy</c> command that needs it.
    ///
    /// <para>
    /// WHY. <c>Strategy.Activate()</c> first runs <c>Strategy.CanBeActivated</c>, whose first
    /// statement reads <c>Administration.Instance.ActiveStrategyCount</c> and
    /// <c>MaxActiveStrategies</c>. That singleton is the Administration building's UI screen
    /// and exists only while a player has the building open, so a seam call from the bare
    /// Space Center would throw inside stock. The dispatcher defers
    /// <c>administration-not-ready</c> while the singleton is null; on that defer this file
    /// instantiates the screen's canvas from the scene's own
    /// <c>AdministrationSceneSpawner</c> prefab with the canvas DISABLED, so Awake / Start run
    /// (Instance set, the slot limit and commitment ceiling read from the Administration level
    /// exactly as when the player opens it) and nothing draws. The in-game
    /// <c>StrategyLifecycle</c> category hosts the same screen the same way.
    /// </para>
    ///
    /// <para>
    /// LIFETIME. One command. The KscAction executor releases it after the stock call, and a
    /// scene change releases it too (the canvas is parented under the persistent
    /// <c>UIMasterController</c> main canvas, so it would otherwise outlive the scene). A
    /// fresh screen per command matters: stock's screen counts active strategies once, in
    /// Start, so a screen kept across an activation would report a stale slot count.
    /// </para>
    /// </summary>
    public partial class ParsekTestCommandAddon
    {
        private Canvas hiddenAdministrationCanvas;

        // Instantiate the hidden screen once per defer run. Logged at every outcome, because a
        // head that sits deferring on administration-not-ready until TIMEOUT is otherwise
        // silent about why.
        private void EnsureHiddenAdministrationScreen(string headId)
        {
            if (hiddenAdministrationCanvas != null) return;
            if (KSP.UI.Screens.Administration.Instance != null) return;
            if (HighLogic.LoadedScene != GameScenes.SPACECENTER) return;

            string failure = null;
            try
            {
                var uiMaster = KSP.UI.UIMasterController.Instance;
                var spawner = UnityEngine.Object.FindObjectOfType<KSP.UI.Screens.AdministrationSceneSpawner>();
                var screenPrefab = spawner != null ? spawner.AdministrationScreenPrefab : null;
                var canvasPrefab = screenPrefab != null ? screenPrefab.canvas : null;
                if (uiMaster == null || uiMaster.mainCanvas == null)
                    failure = "no-ui-master-canvas";
                else if (spawner == null)
                    failure = "no-administration-spawner";
                else if (canvasPrefab == null)
                    failure = "no-administration-prefab";
                else
                {
                    Canvas canvas = UnityEngine.Object.Instantiate(canvasPrefab);
                    canvas.enabled = false;
                    canvas.gameObject.name = "ParsekSeamHiddenAdministration";
                    var rt = (RectTransform)canvas.transform;
                    rt.SetParent(uiMaster.mainCanvas.transform, worldPositionStays: false);
                    rt.SetAsLastSibling();
                    hiddenAdministrationCanvas = canvas;
                }
            }
            catch (Exception ex)
            {
                failure = "threw " + ex.GetType().Name + ": " + ex.Message;
            }

            if (failure != null)
                ParsekLog.WarnRateLimited(Tag, "kscaction-admin-host-" + failure,
                    "kscaction hidden Administration screen not hosted: " + failure + " head=" + (headId ?? ""));
            else
                ParsekLog.Info(Tag, "kscaction hidden Administration screen hosted for head=" + (headId ?? ""));
        }

        private void ReleaseHiddenAdministrationScreen(string context)
        {
            if (hiddenAdministrationCanvas == null) return;
            try
            {
                UnityEngine.Object.Destroy(hiddenAdministrationCanvas.gameObject);
                ParsekLog.Info(Tag, "kscaction hidden Administration screen released (" + context + ")");
            }
            catch (Exception ex)
            {
                ParsekLog.Warn(Tag, "kscaction hidden Administration screen release threw (" + context + "): "
                    + ex.GetType().Name + ": " + ex.Message);
            }
            finally
            {
                hiddenAdministrationCanvas = null;
            }
        }
    }
}
