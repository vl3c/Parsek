using System;
using System.Collections;
using System.Collections.Generic;
using System.Globalization;
using System.Reflection;
using HarmonyLib;
using KSP.UI;
using KSP.UI.Screens;
using PreFlightTests;
using UnityEngine;
using Object = UnityEngine.Object;

namespace Parsek.TestCommands
{
    /// <summary>
    /// The thin Unity applier for the two-phase <c>LaunchSiteProbe</c> verb. Every decision is
    /// in the pure <see cref="TestCommandLaunchSiteProbe"/>; this file samples live state,
    /// presses stock's own buttons and reads stock's own dialog.
    ///
    /// <para>STOCK PATHS, NOT SHORTCUTS. The editor's Launch is <c>EditorLogic.launchVessel</c>
    /// (the Launch button's handler); the Space Center's is the launch-site building's
    /// <c>EnterBuilding</c> (its click), the picker row's own selection body
    /// (<c>VesselSpawnDialog.SelectVesselDataItem</c>) and the picker's Launch button
    /// (<c>buttonLaunch.onClick</c>, which runs <c>ButtonLaunch</c> -> the craft pipeline ->
    /// <c>LaunchSiteFacility.shipSelected</c>). A dialog button is pressed through its own
    /// <c>OptionSelected</c>, which runs the stock callback and dismisses the popup exactly as a
    /// click does, minus the interactable check a greyed button applies to a real click.</para>
    /// </summary>
    public partial class ParsekTestCommandAddon
    {
        private enum LaunchSiteProbeStage
        {
            WaitLaunchUnlocked,
            OpenPicker,
            SelectCraft,
            AwaitDialog,
            Settle,
        }

        private sealed class LaunchSiteProbePending
        {
            internal LaunchSiteProbeRequest Request;
            internal LaunchSiteProbeStage Stage;
            internal int StageStartFrame;
            internal double StageStartTime;
            internal GameScenes StartScene;
            internal LaunchSiteFacility Facility;
            internal string Site;
            internal int ObstructingBefore;
            internal string FirstBefore;
            internal string DialogName;
            internal int ButtonCount;
            internal bool ProceedOffered;
            internal string ProceedInteractable;
            internal string Pressed;
        }

        private LaunchSiteProbePending launchSiteProbePending;

        private static readonly AccessTools.FieldRef<EditorLogic, LaunchSiteClear> EditorLaunchSiteTestRef =
            AccessTools.FieldRefAccess<EditorLogic, LaunchSiteClear>("launchSiteClearTest");
        private static readonly AccessTools.FieldRef<LaunchSiteFacility, LaunchSiteClear> FacilityLaunchSiteTestRef =
            AccessTools.FieldRefAccess<LaunchSiteFacility, LaunchSiteClear>("launchSiteClearTest");

        private void ResetLaunchSiteProbeState()
        {
            launchSiteProbePending = null;
        }

        // Stock's own obstruction predicate (the one LaunchSiteClear.Test calls).
        private static int CountObstructions(string site, out string first)
        {
            first = null;
            if (string.IsNullOrEmpty(site) || HighLogic.CurrentGame == null || HighLogic.CurrentGame.flightState == null)
                return 0;
            List<ProtoVessel> found = ShipConstruction.FindVesselsLandedAt(HighLogic.CurrentGame.flightState, site);
            if (found == null || found.Count == 0) return 0;
            if (found[0] != null) first = found[0].vesselName;
            return found.Count;
        }

        private void LaunchSiteProbeImpl(ParsedCommand cmd)
        {
            string sceneName = HighLogic.LoadedScene.ToString();
            TestCommandScene scene = MapScene(HighLogic.LoadedScene);
            if (!TestCommandLaunchSiteProbe.TryParse(scene,
                    ArgOrNull(cmd, TestCommandLaunchSiteProbe.FacilityArg),
                    ArgOrNull(cmd, TestCommandLaunchSiteProbe.CraftArg),
                    ArgOrNull(cmd, TestCommandLaunchSiteProbe.AnswerArg),
                    out LaunchSiteProbeRequest request, out string reject, out string detail))
            {
                ParsekLog.Warn(Tag, $"launchsiteprobe rejected reason={reject} {detail}");
                SetExecResult("REJECTED", null, reject + " " + detail);
                return;
            }

            var p = new LaunchSiteProbePending
            {
                Request = request,
                StartScene = HighLogic.LoadedScene,
                StageStartFrame = Time.frameCount,
                StageStartTime = WallClockSeconds(),
            };

            if (request.Path == LaunchSiteProbePath.Editor)
            {
                EditorLogic editor = EditorLogic.fetch;
                int parts = editor != null && editor.ship != null ? editor.ship.parts.Count : 0;
                bool unlocked = InputLockManager.IsUnlocked(ControlTypes.EDITOR_LAUNCH);
                p.Site = editor != null ? editor.launchSiteName : null;
                p.ObstructingBefore = CountObstructions(p.Site, out p.FirstBefore);
                string gate = TestCommandLaunchSiteProbe.DecideEditorGate(editor != null, parts, unlocked, p.ObstructingBefore);
                bool lockOnly = TestCommandLaunchSiteProbe.EditorLockIsOnlyBlocker(editor != null, parts, unlocked, p.ObstructingBefore);
                if (gate != null && !lockOnly)
                {
                    string why = "scene=" + sceneName + " parts=" + Int(parts) + " unlocked=" + Bool(unlocked)
                        + " site=" + TestCommandLaunchSiteProbe.Token(p.Site) + " obstructing=" + Int(p.ObstructingBefore);
                    ParsekLog.Warn(Tag, $"launchsiteprobe rejected reason={gate} {why}");
                    SetExecResult("REJECTED", null, gate + " " + why);
                    return;
                }
                ParsekLog.Info(Tag, TestCommandLaunchSiteProbe.FormatStartLine(
                    request, sceneName, p.Site, p.ObstructingBefore, p.FirstBefore));
                launchSiteProbePending = p;
                if (lockOnly)
                {
                    ParsekLog.Verbose(Tag, "launchsiteprobe lock-wait start - EDITOR_LAUNCH is the only blocker; re-checking every frame for up to "
                        + TestCommandLaunchSiteProbe.LaunchLockWaitSeconds.ToString("F0", CultureInfo.InvariantCulture) + "s");
                    p.Stage = LaunchSiteProbeStage.WaitLaunchUnlocked;
                }
                else
                {
                    PressEditorLaunch(p);
                }
                SetExecResult(PendingVerdict, null, null);
                return;
            }

            // Space Center: the facility's own building, its picker not already up.
            LaunchSiteFacility facility = null;
            EditorFacility want = request.Facility == EditorRouteFacility.SPH ? EditorFacility.SPH : EditorFacility.VAB;
            foreach (LaunchSiteFacility f in Object.FindObjectsOfType<LaunchSiteFacility>())
                if (f != null && f.facilityType == want) { facility = f; break; }
            VesselSpawnDialog picker = VesselSpawnDialog.Instance;
            bool pickerOpen = picker != null && picker.Visible;
            string scGate = TestCommandLaunchSiteProbe.DecideSpaceCenterGate(facility != null, pickerOpen);
            if (scGate != null)
            {
                string why = "facility=" + TestCommandEditorRoute.FacilityToken(request.Facility)
                    + " found=" + Bool(facility != null) + " pickerOpen=" + Bool(pickerOpen);
                ParsekLog.Warn(Tag, $"launchsiteprobe rejected reason={scGate} {why}");
                SetExecResult("REJECTED", null, scGate + " " + why);
                return;
            }
            p.Facility = facility;
            ParsekLog.Info(Tag, TestCommandLaunchSiteProbe.FormatStartLine(request, sceneName, facility.launchSiteName, 0, null)
                + " - opening the facility's launch-site picker (the building's own EnterBuilding)");
            facility.EnterBuilding();
            p.Stage = LaunchSiteProbeStage.OpenPicker;
            launchSiteProbePending = p;
            SetExecResult(PendingVerdict, null, null);
        }

        private void PressEditorLaunch(LaunchSiteProbePending p)
        {
            ParsekLog.Info(Tag, "launchsiteprobe pressing Launch path=editor site=" + TestCommandLaunchSiteProbe.Token(p.Site)
                + " - the Launch button's handler (EditorLogic.launchVessel), stock's pre-flight checks run");
            p.Stage = LaunchSiteProbeStage.AwaitDialog;
            p.StageStartFrame = Time.frameCount;
            p.StageStartTime = WallClockSeconds();
            EditorLogic.fetch.launchVessel();
        }

        private void TryCompleteLaunchSiteProbe(double now)
        {
            LaunchSiteProbePending p = launchSiteProbePending;
            if (p == null)
            {
                FinishLaunchSiteProbe(null, "ERROR", TestCommandLaunchSiteProbe.NotSettledReason + " no-pending-state", null, now);
                return;
            }
            double inStage = now - p.StageStartTime;
            int framesInStage = Time.frameCount - p.StageStartFrame;
            bool sceneLeft = HighLogic.LoadedScene != p.StartScene;
            switch (p.Stage)
            {
                case LaunchSiteProbeStage.WaitLaunchUnlocked:
                {
                    if (sceneLeft)
                    {
                        FinishLaunchSiteProbe(p, "ERROR", TestCommandLaunchSiteProbe.NotSettledReason + " scene-left-before-launch", null, now);
                        return;
                    }
                    if (InputLockManager.IsUnlocked(ControlTypes.EDITOR_LAUNCH))
                    {
                        ParsekLog.Info(Tag, "launchsiteprobe lock-wait cleared waited="
                            + inStage.ToString("F2", CultureInfo.InvariantCulture) + "s frames=" + Int(framesInStage));
                        PressEditorLaunch(p);
                        return;
                    }
                    if (inStage >= TestCommandLaunchSiteProbe.LaunchLockWaitSeconds)
                    {
                        string holders = TestCommandEditorRoute.FormatLockHolders(InputLockManager.lockStack,
                            TestCommandEditorRoute.EditorLaunchLockBit, TestCommandEditorRoute.MaxLockHoldersListed);
                        FinishLaunchSiteProbe(p, "REJECTED", TestCommandLaunchSiteProbe.LaunchLockedReason + " " + holders
                            + " waited=" + inStage.ToString("F1", CultureInfo.InvariantCulture) + "s", null, now);
                    }
                    return;
                }
                case LaunchSiteProbeStage.OpenPicker:
                {
                    if (sceneLeft)
                    {
                        FinishLaunchSiteProbe(p, "ERROR", TestCommandLaunchSiteProbe.NotSettledReason + " scene-left-before-launch", null, now);
                        return;
                    }
                    VesselSpawnDialog picker = VesselSpawnDialog.Instance;
                    IList items = picker != null && picker.Visible ? VesselDataItems(picker) : null;
                    int count = items != null ? items.Count : 0;
                    object match = null;
                    var listedNames = new List<string>();
                    for (int i = 0; i < count; i++)
                    {
                        object item = items[i];
                        if (item == null) continue;
                        FieldInfo nameField = item.GetType().GetField("name");
                        FieldInfo pathField = item.GetType().GetField("fullFilePath");
                        string listedName = nameField != null ? nameField.GetValue(item) as string : null;
                        string listedPath = pathField != null ? pathField.GetValue(item) as string : null;
                        if (listedNames.Count < 8) listedNames.Add(TestCommandLaunchSiteProbe.Token(listedName));
                        // A stock craft lists as its localized name plus stock's "(Stock)" suffix,
                        // so the row also matches on the craft file's stem.
                        if (TestCommandLaunchSiteProbe.MatchesPickerRow(p.Request.Craft, listedName, listedPath))
                        {
                            match = item;
                            break;
                        }
                    }
                    if (match != null)
                    {
                        MethodInfo select = AccessTools.Method(typeof(VesselSpawnDialog), "SelectVesselDataItem");
                        if (select == null)
                        {
                            ClosePickerIfOpen("SelectVesselDataItem not found");
                            FinishLaunchSiteProbe(p, "ERROR", TestCommandLaunchSiteProbe.ThrewReason + " SelectVesselDataItem not found", null, now);
                            return;
                        }
                        select.Invoke(picker, new[] { match });
                        ParsekLog.Info(Tag, "launchsiteprobe picker selected craft=" + TestCommandLaunchSiteProbe.Token(p.Request.Craft)
                            + " listed=" + Int(count) + " site=" + TestCommandLaunchSiteProbe.Token(p.Facility != null ? p.Facility.launchSiteName : null));
                        p.Stage = LaunchSiteProbeStage.SelectCraft;
                        p.StageStartFrame = Time.frameCount;
                        p.StageStartTime = now;
                        return;
                    }
                    if (inStage >= TestCommandLaunchSiteProbe.CraftListWaitSeconds)
                    {
                        ClosePickerIfOpen("craft not listed");
                        FinishLaunchSiteProbe(p, "REJECTED", TestCommandLaunchSiteProbe.CraftNotFoundReason
                            + " craft=" + TestCommandLaunchSiteProbe.Token(p.Request.Craft) + " listed=" + Int(count)
                            + " pickerOpen=" + Bool(picker != null && picker.Visible)
                            + " names=[" + string.Join(",", listedNames.ToArray()) + "]", null, now);
                    }
                    return;
                }
                case LaunchSiteProbeStage.SelectCraft:
                {
                    if (framesInStage < TestCommandLaunchSiteProbe.SelectSettleFrames) return;
                    p.Site = p.Facility != null ? p.Facility.launchSiteName : null;
                    p.ObstructingBefore = CountObstructions(p.Site, out p.FirstBefore);
                    string siteGate = TestCommandLaunchSiteProbe.DecideSiteGate(p.ObstructingBefore);
                    if (siteGate != null)
                    {
                        ClosePickerIfOpen("site clear");
                        FinishLaunchSiteProbe(p, "REJECTED", siteGate + " site=" + TestCommandLaunchSiteProbe.Token(p.Site)
                            + " - pressing Launch on a clear site launches", null, now);
                        return;
                    }
                    VesselSpawnDialog picker = VesselSpawnDialog.Instance;
                    FieldInfo launchField = AccessTools.Field(typeof(VesselSpawnDialog), "buttonLaunch");
                    var launch = picker != null && launchField != null ? launchField.GetValue(picker) as UnityEngine.UI.Button : null;
                    if (launch == null)
                    {
                        ClosePickerIfOpen("buttonLaunch not found");
                        FinishLaunchSiteProbe(p, "ERROR", TestCommandLaunchSiteProbe.ThrewReason + " buttonLaunch not found", null, now);
                        return;
                    }
                    ParsekLog.Info(Tag, "launchsiteprobe pressing Launch path=spacecenter craft=" + TestCommandLaunchSiteProbe.Token(p.Request.Craft)
                        + " site=" + TestCommandLaunchSiteProbe.Token(p.Site) + " obstructing=" + Int(p.ObstructingBefore)
                        + " first=" + TestCommandLaunchSiteProbe.Token(p.FirstBefore)
                        + " launchInteractable=" + Bool(launch.interactable)
                        + " - the picker's own Launch button (ButtonLaunch -> LaunchSiteFacility.shipSelected)");
                    p.Stage = LaunchSiteProbeStage.AwaitDialog;
                    p.StageStartFrame = Time.frameCount;
                    p.StageStartTime = now;
                    launch.onClick.Invoke();
                    return;
                }
                case LaunchSiteProbeStage.AwaitDialog:
                {
                    PopupDialog target = null;
                    PopupDialog other = null;
                    string expectedTitle = null;
                    LaunchSiteClear test = LiveLaunchSiteTest(p);
                    if (test != null) expectedTitle = test.GetWarningTitle();
                    string targetName = p.Request.Path == LaunchSiteProbePath.SpaceCenter
                        ? TestCommandLaunchSiteProbe.SpaceCenterDialogName
                        : TestCommandLaunchSiteProbe.EditorDialogName;
                    if (!sceneLeft)
                    {
                        foreach (PopupDialog popup in Object.FindObjectsOfType<PopupDialog>())
                        {
                            MultiOptionDialog d = popup != null ? popup.dialogToDisplay : null;
                            if (d == null || d.name != targetName) continue;
                            // The editor's PreFlightCheck names every failed test's dialog
                            // TestWarning; the launch-site one carries the test's own title.
                            if (p.Request.Path == LaunchSiteProbePath.Editor
                                && !string.Equals(d.title, expectedTitle, StringComparison.Ordinal))
                            {
                                other = popup;
                                continue;
                            }
                            target = popup;
                            break;
                        }
                    }
                    LaunchSiteProbeAwaitOutcome outcome = TestCommandLaunchSiteProbe.DecideAwait(
                        sceneLeft, target != null, other != null, inStage);
                    switch (outcome)
                    {
                        case LaunchSiteProbeAwaitOutcome.NotYet:
                            return;
                        case LaunchSiteProbeAwaitOutcome.Launched:
                            ParsekLog.Warn(Tag, "launchsiteprobe launched without a launch-site dialog path="
                                + TestCommandLaunchSiteProbe.PathToken(p.Request.Path) + " scene=" + HighLogic.LoadedScene
                                + " - stock's launch-site test passed and the launch went ahead");
                            p.DialogName = "none";
                            p.Pressed = "none";
                            p.ProceedInteractable = "-";
                            FinishLaunchSiteProbe(p, "OK", null, "launched", now);
                            return;
                        case LaunchSiteProbeAwaitOutcome.OtherDialog:
                        {
                            string otherTitle = other.dialogToDisplay != null ? other.dialogToDisplay.title : null;
                            List<DialogGUIButton> otherButtons = GetDialogButtons(other);
                            if (otherButtons.Count > 0 && DialogGuiButtonOptionSelectedMethod != null)
                                DialogGuiButtonOptionSelectedMethod.Invoke(otherButtons[otherButtons.Count - 1], null);
                            FinishLaunchSiteProbe(p, "ERROR", TestCommandLaunchSiteProbe.OtherDialogReason
                                + " title=" + TestCommandLaunchSiteProbe.Token(TestCommandLaunchSiteProbe.OneLine(otherTitle))
                                + " expected=" + TestCommandLaunchSiteProbe.Token(TestCommandLaunchSiteProbe.OneLine(expectedTitle))
                                + " - another pre-flight test failed first; its last button was pressed", null, now);
                            return;
                        }
                        case LaunchSiteProbeAwaitOutcome.TimedOut:
                            ClosePickerIfOpen("no dialog");
                            FinishLaunchSiteProbe(p, "ERROR", TestCommandLaunchSiteProbe.NoDialogReason
                                + " name=" + targetName + " open=" + TestCommandLaunchSiteProbe.Token(OpenPopupNames())
                                + " waited=" + inStage.ToString("F1", CultureInfo.InvariantCulture) + "s", null, now);
                            return;
                    }
                    ReadAndPressLaunchSiteDialog(p, target, test, now);
                    return;
                }
                case LaunchSiteProbeStage.Settle:
                {
                    string dialogName = p.Request.Path == LaunchSiteProbePath.SpaceCenter
                        ? TestCommandLaunchSiteProbe.SpaceCenterDialogName
                        : TestCommandLaunchSiteProbe.EditorDialogName;
                    bool dialogOpen = !sceneLeft && FindPopupByName(dialogName) != null;
                    bool expired = DeferralBudget.ShouldTimeout(completionStartedAt, now,
                        DeferralBudget.BudgetSeconds(TestCommandLaunchSiteProbe.Verb));
                    LaunchSiteProbeSettleOutcome settle = TestCommandLaunchSiteProbe.DecideSettle(
                        sceneLeft, dialogOpen, framesInStage, inStage, expired);
                    if (settle == LaunchSiteProbeSettleOutcome.NotYet) return;
                    if (settle == LaunchSiteProbeSettleOutcome.TimedOut)
                    {
                        FinishLaunchSiteProbe(p, "ERROR", TestCommandLaunchSiteProbe.NotSettledReason
                            + " dialogOpen=" + Bool(dialogOpen) + " pressed=" + TestCommandLaunchSiteProbe.Token(p.Pressed), null, now);
                        return;
                    }
                    if (settle == LaunchSiteProbeSettleOutcome.Stayed && p.Request.Path == LaunchSiteProbePath.SpaceCenter)
                        ClosePickerIfOpen("probe settled");
                    FinishLaunchSiteProbe(p, "OK", null,
                        settle == LaunchSiteProbeSettleOutcome.Launched ? "launched" : "stayed", now);
                    return;
                }
            }
        }

        private static LaunchSiteClear LiveLaunchSiteTest(LaunchSiteProbePending p)
        {
            try
            {
                if (p.Request.Path == LaunchSiteProbePath.Editor)
                    return EditorLogic.fetch != null ? EditorLaunchSiteTestRef(EditorLogic.fetch) : null;
                return p.Facility != null ? FacilityLaunchSiteTestRef(p.Facility) : null;
            }
            catch (Exception)
            {
                return null;
            }
        }

        // Reads the dialog (labels, interactable states, title, description), logs it, and
        // presses the chosen button through its own callback.
        private void ReadAndPressLaunchSiteDialog(LaunchSiteProbePending p, PopupDialog popup, LaunchSiteClear test,
            double now)
        {
            MultiOptionDialog dialog = popup.dialogToDisplay;
            List<DialogGUIButton> buttons = GetDialogButtons(popup);
            var labels = new List<string>(buttons.Count);
            var interactable = new List<bool>(buttons.Count);
            for (int i = 0; i < buttons.Count; i++)
            {
                DialogGUIButton b = buttons[i];
                labels.Add(b != null ? b.OptionText : null);
                bool on = true;
                try
                {
                    // Stock's own source for the button's interactable state (DialogGUIBase.Update
                    // applies it to the uGUI button every frame).
                    if (b != null && b.OptionInteractableCondition != null) on = b.OptionInteractableCondition();
                }
                catch (Exception)
                {
                    on = true;
                }
                interactable.Add(on);
            }
            string abortLabel = test != null ? test.GetAbortOption() : null;
            LaunchSiteProbeButtons roles = TestCommandLaunchSiteProbe.ClassifyButtons(p.Request.Path, labels, abortLabel);
            p.DialogName = dialog != null ? dialog.name : null;
            p.ButtonCount = labels.Count;
            p.ProceedOffered = roles.ProceedIndex >= 0;
            p.ProceedInteractable = TestCommandLaunchSiteProbe.ProceedInteractableToken(roles, interactable);
            ParsekLog.Info(Tag, TestCommandLaunchSiteProbe.FormatDialogLine(p.Request.Path, p.DialogName,
                dialog != null ? dialog.title : null, labels, interactable, roles, dialog != null ? dialog.message : null));

            if (!roles.ShapeOk || DialogGuiButtonOptionSelectedMethod == null)
            {
                // Leave nothing modal behind: the last button is abort / cancel in both shapes.
                if (buttons.Count > 0 && DialogGuiButtonOptionSelectedMethod != null)
                    DialogGuiButtonOptionSelectedMethod.Invoke(buttons[buttons.Count - 1], null);
                ClosePickerIfOpen("dialog shape");
                FinishLaunchSiteProbe(p, "ERROR", TestCommandLaunchSiteProbe.DialogShapeReason
                    + " buttons=" + Int(labels.Count) + " labels=" + TestCommandLaunchSiteProbe.Token(TestCommandLaunchSiteProbe.FormatLabels(labels))
                    + " abort=" + TestCommandLaunchSiteProbe.Token(abortLabel), null, now);
                return;
            }

            int index = TestCommandLaunchSiteProbe.ChooseButton(p.Request.Press, roles, out string pressedToken);
            p.Pressed = pressedToken;
            ParsekLog.Info(Tag, TestCommandLaunchSiteProbe.FormatPressLine(p.Request.Path, pressedToken,
                labels[index], interactable[index], p.Request.Press));
            p.Stage = LaunchSiteProbeStage.Settle;
            p.StageStartFrame = Time.frameCount;
            p.StageStartTime = now;
            DialogGuiButtonOptionSelectedMethod.Invoke(buttons[index], null);
        }

        // The picker's own Close button, when the picker is still up (a probe that ends
        // before or after the launch dialog never leaves it open behind the next step).
        private void ClosePickerIfOpen(string why)
        {
            try
            {
                VesselSpawnDialog picker = VesselSpawnDialog.Instance;
                if (picker == null || !picker.Visible) return;
                FieldInfo closeField = AccessTools.Field(typeof(VesselSpawnDialog), "buttonClose");
                var close = closeField != null ? closeField.GetValue(picker) as UnityEngine.UI.Button : null;
                if (close == null) return;
                ParsekLog.Info(Tag, "launchsiteprobe closing the launch-site picker (" + why + ") through its own Close button");
                close.onClick.Invoke();
            }
            catch (Exception ex)
            {
                ParsekLog.Warn(Tag, "launchsiteprobe picker close failed: " + ex.GetType().Name + ": " + ex.Message);
            }
        }

        private static string OpenPopupNames()
        {
            var names = new List<string>();
            foreach (PopupDialog popup in Object.FindObjectsOfType<PopupDialog>())
            {
                MultiOptionDialog d = popup != null ? popup.dialogToDisplay : null;
                if (d != null && !string.IsNullOrEmpty(d.name)) names.Add(d.name.Replace(' ', '_'));
            }
            return names.Count > 0 ? string.Join(",", names.ToArray()) : null;
        }

        private void FinishLaunchSiteProbe(LaunchSiteProbePending p, string verdict, string msg, string outcome, double now)
        {
            string id = completionId; long seq = completionSeq; string verb = completionVerb;
            double elapsed = now - completionStartedAt;
            launchSiteProbePending = null;
            ClearTwoPhase();
            if (verdict != "OK" || p == null)
            {
                if (verdict == "REJECTED") ParsekLog.Warn(Tag, "launchsiteprobe rejected reason=" + msg);
                else ParsekLog.Error(Tag, "launchsiteprobe error " + msg + " elapsed="
                    + elapsed.ToString("F1", CultureInfo.InvariantCulture) + "s");
                EmitExecutedTerminal(id, seq, verb, verdict == "REJECTED" ? "REJECTED" : "ERROR", null, msg, dequeueHead: true);
                return;
            }
            bool sceneSame = HighLogic.LoadedScene == p.StartScene;
            string firstAfter = null;
            int after = CountObstructions(p.Site, out firstAfter);
            bool lockHeld = sceneSame && InputLockManager.lockStack != null
                && InputLockManager.lockStack.ContainsKey(p.Request.Path == LaunchSiteProbePath.SpaceCenter
                    ? TestCommandLaunchSiteProbe.FacilityLockId
                    : TestCommandLaunchSiteProbe.EditorLaunchSequenceLockId);
            ParsekLog.Info(Tag, TestCommandLaunchSiteProbe.FormatCompleteLine(p.Request.Path, p.Site, p.DialogName,
                p.ButtonCount, p.ProceedOffered, p.ProceedInteractable, p.Pressed, outcome,
                p.ObstructingBefore, after, firstAfter, lockHeld, elapsed));
            EmitExecutedTerminal(id, seq, verb, "OK",
                TestCommandLaunchSiteProbe.BuildPayload(p.Request.Path, p.Site, p.DialogName, p.ButtonCount,
                    p.ProceedOffered, p.ProceedInteractable, p.Pressed, outcome, p.ObstructingBefore, after),
                null, dequeueHead: true);
        }
    }
}
