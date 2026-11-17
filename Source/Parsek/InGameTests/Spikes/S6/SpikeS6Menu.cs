using System;
using System.Collections;
using System.Collections.Generic;
using System.IO;
using System.Reflection;
using System.Runtime.CompilerServices;
using HarmonyLib;
using UnityEngine;

namespace Parsek.InGameTests.Spikes.S6
{
    // R0 spike branch only (spike/mp-r0, never merged). Plan: r0-spikes.md, task S6 steps
    // (c), (d), (e) and the main-menu half of (f).
    //
    // Lives on the armed S6 host object (never a KSPAddon, so an unarmed DLL never runs it).
    // Each time a MainMenu instance appears it: logs the bodies (e), dumps content once (f),
    // clones scenariosBtn into a "Multiplayer" entry of the private stageTwoBtn list (c),
    // and, with PARSEK_SPIKE_S6_AUTO=1, drives the entry by keyboard events, fills the typed
    // dialog, opens the stock difficulty panel over it (d), and creates and starts the game.
    // On the visit after the created game's Space Center it resumes that save through the
    // stock load-dialog sequence. TMPro is not referenced by Parsek, so the button label and
    // the input fields are reached by reflection.
    internal sealed class SpikeS6Menu : MonoBehaviour
    {
        private MainMenu menu;
        private TextProButton3D clone;
        private bool lastHovered;
        private bool autoAccepting;
        private float lastMouseUp = -10f;
        private float lastEnterDown = -10f;

        private PopupDialog dialog;
        private object folderInput;
        private object saveInput;
        private string folderText = "";
        private string saveText = "";
        private Game.Modes mode = Game.Modes.CAREER;
        private GameParameters pars;
        private List<KeyValuePair<string, string>> defaultsRead;
        private List<KeyValuePair<string, string>> callbackRead;
        private bool diffDone;
        private bool diffAccepted;
        private DifficultyOptionsMenu dom;

        private static bool contentDumped;
        private static bool autoStarted;

        void Awake()
        {
            GameEvents.OnDifficultySettingsShown.Add(OnDifficultyShown);
        }

        void OnDestroy()
        {
            GameEvents.OnDifficultySettingsShown.Remove(OnDifficultyShown);
        }

        void Update()
        {
            if (HighLogic.LoadedScene != GameScenes.MAINMENU) return;
            // TextProButton3D taps from a coroutine a little after the release, so the
            // tap handler reads how recently the mouse or Enter was used, not this frame.
            if (Input.GetMouseButtonUp(0)) lastMouseUp = Time.realtimeSinceStartup;
            if (Input.GetKeyDown(KeyCode.Return) || Input.GetKeyDown(KeyCode.KeypadEnter)) lastEnterDown = Time.realtimeSinceStartup;
            if (menu == null)
            {
                MainMenu found = FindObjectOfType<MainMenu>();
                if (found != null)
                {
                    menu = found;
                    clone = null;
                    StartCoroutine(MainMenuFlow());
                }
                return;
            }
            if (clone != null && clone.isBeingHovered != lastHovered)
            {
                lastHovered = clone.isBeingHovered;
                SpikeS6.Info("menu clone hovered=" + lastHovered);
            }
        }

        private IEnumerator MainMenuFlow()
        {
            for (int i = 0; i < 60; i++) yield return null;
            if (menu == null) yield break;
            SpikeS6.Info("mainmenu settled uiScale=" + SpikeS6.Inv((double)GameSettings.UI_SCALE));
            LogBodies();

            var created = SpikeS6State.Created;
            if (created != null && created.ResumePending)
            {
                ResumeLikeLoadDialog(created);
                yield break;
            }

            CloneEntry();

            if (SpikeS6.Options.Content && !contentDumped)
            {
                contentDumped = true;
                SpikeS6ContentDump.WriteMainMenu(SpikeS6.Options.Label);
            }

            if (SpikeS6.Options.Auto && !autoStarted)
            {
                autoStarted = true;
                yield return StartCoroutine(AutoDrive());
            }
        }

        // ------------------------------------------------------------ (e) bodies

        private static void LogBodies()
        {
            try
            {
                int count = FlightGlobals.Bodies != null ? FlightGlobals.Bodies.Count : -1;
                CelestialBody home = null;
                try { home = FlightGlobals.GetHomeBody(); } catch (Exception) { }
                SpikeS6.Info("bodies scene=MAINMENU count=" + SpikeS6.Inv(count)
                    + " home=" + (home != null ? home.bodyName : "<null>")
                    + " homeRadius=" + (home != null ? SpikeS6.Inv(home.Radius) : "-"));
                SpikeS6.Csv("bodies.csv", new[] { "scene", "count", "home", "homeRadius" }, "MAINMENU", count,
                    home != null ? home.bodyName : "", home != null ? home.Radius : double.NaN);
            }
            catch (Exception ex)
            {
                SpikeS6.Warn("bodies read failed: " + ex.GetType().Name + ": " + ex.Message);
            }
        }

        // ------------------------------------------------------------ (c) menu clone

        private void CloneEntry()
        {
            try
            {
                FieldInfo stageField = AccessTools.Field(typeof(MainMenu), "stageTwoBtn");
                var list = stageField != null ? stageField.GetValue(menu) as List<TextProButton3D> : null;
                TextProButton3D src = menu.scenariosBtn;
                if (list == null || src == null)
                {
                    SpikeS6.Warn("menu clone failed: stageTwoBtn=" + (list != null) + " scenariosBtn=" + (src != null)
                        + " (fallback in plan: IMGUI button)");
                    return;
                }
                int idx = list.IndexOf(src);
                int before = list.Count;
                if (idx < 0)
                {
                    SpikeS6.Warn("menu clone: scenariosBtn not in stageTwoBtn (count=" + SpikeS6.Inv(before) + "); appending");
                    idx = list.Count - 1;
                }
                Vector3 delta;
                if (idx + 1 < list.Count) delta = list[idx + 1].transform.position - list[idx].transform.position;
                else if (idx > 0) delta = list[idx].transform.position - list[idx - 1].transform.position;
                else delta = new Vector3(0f, -0.5f, 0f);

                GameObject go = Instantiate(src.gameObject, src.transform.parent);
                go.name = "SpikeS6MultiplayerBtn";
                go.transform.position = list[idx].transform.position + delta;
                for (int j = idx + 1; j < list.Count; j++) list[j].transform.position += delta;
                clone = go.GetComponent<TextProButton3D>();
                list.Insert(idx + 1, clone);
                clone.onTap = OnMultiplayerTap;
                SetLabel(clone, "Multiplayer");
                lastHovered = clone.isBeingHovered;

                var names = new List<string>();
                foreach (var b in list) names.Add(b == null ? "<null>" : b.name);
                SpikeS6.Info("menu clone ok stageTwo=" + SpikeS6.Inv(before) + "->" + SpikeS6.Inv(list.Count)
                    + " index=" + SpikeS6.Inv(idx + 1) + " delta=" + delta.ToString("F3")
                    + " label=" + (ReadLabel(clone) ?? "<unread>") + " order=[" + string.Join(",", names.ToArray()) + "]");
                StartCoroutine(ReadLabelLater());
            }
            catch (Exception ex)
            {
                SpikeS6.Warn("menu clone threw " + ex.GetType().Name + ": " + ex.Message);
            }
        }

        private IEnumerator ReadLabelLater()
        {
            for (int i = 0; i < 60; i++) yield return null;
            if (clone != null)
                SpikeS6.Info("menu clone label after 60 frames=" + (ReadLabel(clone) ?? "<unread>")
                    + " active=" + clone.gameObject.activeInHierarchy);
        }

        private static object TmpOf(TextProButton3D b)
        {
            FieldInfo f = typeof(TextProButton3D).GetField("text", BindingFlags.Instance | BindingFlags.NonPublic);
            return f != null && b != null ? f.GetValue(b) : null;
        }

        private static void SetLabel(TextProButton3D b, string label)
        {
            object tmp = TmpOf(b);
            PropertyInfo p = tmp != null ? tmp.GetType().GetProperty("text") : null;
            if (p == null) { SpikeS6.Warn("menu clone: label property missing (TMPro text)"); return; }
            p.SetValue(tmp, label, null);
        }

        private static string ReadLabel(TextProButton3D b)
        {
            object tmp = TmpOf(b);
            PropertyInfo p = tmp != null ? tmp.GetType().GetProperty("text") : null;
            return p != null ? p.GetValue(tmp, null) as string : null;
        }

        private void OnMultiplayerTap()
        {
            float now = Time.realtimeSinceStartup;
            string via = autoAccepting ? "auto-accept"
                : now - lastEnterDown < 1f ? "enter"
                : now - lastMouseUp < 1f ? "mouse" : "unknown";
            SpikeS6.Info("menu tap via=" + via + " arrowsUsed=" + ArrowsUsed());
            OpenDialog();
        }

        private bool ArrowsUsed()
        {
            FieldInfo f = AccessTools.Field(typeof(MainMenu), "hasUsedArrowsOnce");
            return menu != null && f != null && (bool)f.GetValue(menu);
        }

        // ------------------------------------------------------------ the typed dialog

        private void OpenDialog()
        {
            if (dialog != null) { SpikeS6.Info("dialog already open"); return; }
            try
            {
                mode = ParseMode(SpikeS6.Options.Mode);
                pars = GameParameters.GetDefaultParameters(mode, GameParameters.Preset.Normal);
                defaultsRead = SpikeS6Difficulty.ReadLive(pars);
                callbackRead = null;
                diffDone = false;
                LogDifficulty("dialog-defaults", defaultsRead);

                saveText = SpikeS6Options.PickSaveName(SpikeS6.Options.Save, ExistingSaves());
                folderText = "";
                folderInput = MakeTextInput("", 400, 420f, s => { OnFolderChanged(s); return s; });
                saveInput = MakeTextInput(saveText, 64, 420f, s => { saveText = s; return s; });
                if (folderInput == null || saveInput == null)
                    SpikeS6.Warn("dialog: DialogGUITextInput constructor missing; the dialog opens without inputs");

                var items = new List<DialogGUIBase>();
                items.Add(new DialogGUILabel("Race folder (paste a path with Ctrl+V):", false, false));
                if (folderInput != null) items.Add((DialogGUIBase)folderInput);
                items.Add(new DialogGUILabel("Save name:", false, false));
                if (saveInput != null) items.Add((DialogGUIBase)saveInput);
                items.Add(new DialogGUILabel(() => "Difficulty: " + DifficultyStatus(), false, false));
                items.Add(new DialogGUIButton("Difficulty", OpenDifficulty, false));
                items.Add(new DialogGUIButton("Create", Create, true));
                items.Add(new DialogGUIButton("Cancel", () => { SpikeS6.Info("dialog cancelled"); dialog = null; }, true));

                dialog = PopupDialog.SpawnPopupDialog(new Vector2(0.5f, 0.5f), new Vector2(0.5f, 0.5f),
                    new MultiOptionDialog("ParsekSpikeS6Race", "Spike S6: create a race save", "Multiplayer",
                        HighLogic.UISkin, items.ToArray()),
                    false, HighLogic.UISkin);

                try { GUIUtility.systemCopyBuffer = SpikeS6.Options.Folder; } catch (Exception) { }
                SpikeS6.Info("dialog open mode=" + mode + " save=" + saveText + " uiScale="
                    + SpikeS6.Inv((double)GameSettings.UI_SCALE) + " lossyScale="
                    + (dialog != null ? dialog.transform.lossyScale.x.ToString("R", System.Globalization.CultureInfo.InvariantCulture) : "-")
                    + " clipboardPrimedLen=" + SpikeS6.Inv(SpikeS6.Options.Folder.Length));
            }
            catch (Exception ex)
            {
                SpikeS6.Warn("dialog open threw " + ex.GetType().Name + ": " + ex.Message);
            }
        }

        private string DifficultyStatus()
        {
            if (!diffDone) return "defaults";
            return (diffAccepted ? "accepted" : "cancelled") + ", "
                + SpikeS6.Inv(SpikeS6Difficulty.Diff(defaultsRead, callbackRead).Count) + " changed";
        }

        private void OnFolderChanged(string s)
        {
            int prev = folderText != null ? folderText.Length : 0;
            folderText = s ?? "";
            if (Math.Abs(folderText.Length - prev) >= 10)
                SpikeS6.Info("dialog folder changed len=" + SpikeS6.Inv(prev) + "->" + SpikeS6.Inv(folderText.Length)
                    + " equalsPrimed=" + string.Equals(folderText, SpikeS6.Options.Folder, StringComparison.Ordinal));
        }

        // DialogGUITextInput's overloads include TMPro-typed ones, which Parsek cannot bind at
        // compile time; the (string, bool, int, Func, float w, float h) overload is invoked by
        // reflection, with the (.., Func, float h) one as the fallback.
        private static object MakeTextInput(string initial, int maxLength, float width, Func<string, string> onSet)
        {
            Type t = typeof(DialogGUITextInput);
            ConstructorInfo c6 = t.GetConstructor(new[] { typeof(string), typeof(bool), typeof(int), typeof(Func<string, string>), typeof(float), typeof(float) });
            if (c6 != null) return c6.Invoke(new object[] { initial, false, maxLength, onSet, width, 24f });
            ConstructorInfo c5 = t.GetConstructor(new[] { typeof(string), typeof(bool), typeof(int), typeof(Func<string, string>), typeof(float) });
            if (c5 != null) return c5.Invoke(new object[] { initial, false, maxLength, onSet, 24f });
            return null;
        }

        /// <summary>Sets the live TMP_InputField text of a DialogGUITextInput (fires its callback).</summary>
        private static bool SetInputText(object input, string text)
        {
            if (input == null) return false;
            FieldInfo f = typeof(DialogGUITextInput).GetField("field", BindingFlags.Instance | BindingFlags.NonPublic);
            object tmp = f != null ? f.GetValue(input) : null;
            PropertyInfo p = tmp != null ? tmp.GetType().GetProperty("text") : null;
            if (p == null) return false;
            p.SetValue(tmp, text, null);
            return true;
        }

        private static Game.Modes ParseMode(string m)
        {
            switch (m)
            {
                case "SANDBOX": return Game.Modes.SANDBOX;
                case "SCIENCE_SANDBOX": return Game.Modes.SCIENCE_SANDBOX;
                default: return Game.Modes.CAREER;
            }
        }

        private static List<string> ExistingSaves()
        {
            var names = new List<string>();
            try
            {
                string root = Path.Combine(KSPUtil.ApplicationRootPath, "saves");
                if (Directory.Exists(root))
                    foreach (string d in Directory.GetDirectories(root)) names.Add(Path.GetFileName(d));
            }
            catch (Exception) { }
            return names;
        }

        // ------------------------------------------------------------ (d) difficulty panel

        private void OpenDifficulty()
        {
            try
            {
                dom = DifficultyOptionsMenu.Create(mode, pars, true, OnDifficultyDismiss);
                SpikeS6.Info("difficulty open mode=" + mode + " created=" + (dom != null));
            }
            catch (Exception ex)
            {
                SpikeS6.Warn("difficulty open threw " + ex.GetType().Name + ": " + ex.Message
                    + " (fallback in plan: four stock presets)");
            }
        }

        private void OnDifficultyShown(DifficultyOptionsMenu shown)
        {
            StartCoroutine(LogPanelGeometry());
        }

        private IEnumerator LogPanelGeometry()
        {
            PopupDialog panel = null;
            for (int i = 0; i < 120 && panel == null; i++)
            {
                yield return null;
                panel = FindPanel();
            }
            if (panel == null)
            {
                SpikeS6.Warn("difficulty shown but no GameDifficulty popup within 120 frames");
                yield break;
            }
            int toggles = panel.GetComponentsInChildren<UnityEngine.UI.Toggle>(true).Length;
            int sliders = panel.GetComponentsInChildren<UnityEngine.UI.Slider>(true).Length;
            bool samePar = dialog != null && panel.transform.parent == dialog.transform.parent;
            SpikeS6.Info("difficulty shown panel=GameDifficulty toggles=" + SpikeS6.Inv(toggles)
                + " sliders=" + SpikeS6.Inv(sliders) + " dialogAlive=" + (dialog != null)
                + " sameParent=" + samePar
                + " panelSibling=" + SpikeS6.Inv(panel.transform.GetSiblingIndex())
                + " dialogSibling=" + (dialog != null ? SpikeS6.Inv(dialog.transform.GetSiblingIndex()) : "-")
                + " panelOnTop=" + (dialog == null || !samePar || panel.transform.GetSiblingIndex() > dialog.transform.GetSiblingIndex())
                + " uiScale=" + SpikeS6.Inv((double)GameSettings.UI_SCALE)
                + " panelLossyScale=" + panel.transform.lossyScale.x.ToString("R", System.Globalization.CultureInfo.InvariantCulture));
        }

        private static PopupDialog FindPanel()
        {
            foreach (var p in FindObjectsOfType<PopupDialog>())
                if (p != null && p.dialogToDisplay != null && p.dialogToDisplay.name == "GameDifficulty") return p;
            return null;
        }

        private void OnDifficultyDismiss(GameParameters gp, bool accepted)
        {
            try
            {
                bool sameObject = ReferenceEquals(gp, pars);
                callbackRead = SpikeS6Difficulty.ReadLive(gp);
                diffAccepted = accepted;
                diffDone = true;
                var changed = SpikeS6Difficulty.Diff(defaultsRead, callbackRead);
                SpikeS6.Info("difficulty accepted=" + accepted + " sameObject=" + sameObject
                    + " changedFromDefaults=" + SpikeS6.Inv(changed.Count)
                    + (changed.Count > 0 ? " [" + string.Join("; ", changed.ToArray()) + "]" : ""));
                LogDifficulty(accepted ? "callback-accepted" : "callback-cancelled", callbackRead);
                if (gp != null) pars = gp; // Create passes the object the callback handed back
                dom = null;
            }
            catch (Exception ex)
            {
                SpikeS6.Warn("difficulty callback threw " + ex.GetType().Name + ": " + ex.Message);
            }
        }

        internal static void LogDifficulty(string phase, List<KeyValuePair<string, string>> values)
        {
            if (values == null) return;
            foreach (var kv in values)
            {
                SpikeS6.Info("difficulty-value phase=" + phase + " " + kv.Key + "=" + kv.Value);
                SpikeS6.Csv("difficulty.csv", SpikeS6.DifficultyCsvHeader, phase, kv.Key, kv.Value);
            }
        }

        // ------------------------------------------------------------ Create

        private void Create()
        {
            dialog = null;
            try
            {
                string save = SpikeS6Options.PickSaveName(saveText, ExistingSaves());
                if (!string.Equals(save, saveText, StringComparison.Ordinal))
                    SpikeS6.Info("create: save name '" + saveText + "' taken or invalid, using '" + save + "'");
                string flag = menu != null && !string.IsNullOrEmpty(menu.DefaultFlagURL) ? menu.DefaultFlagURL : "Squad/Flags/default";

                Game game = GamePersistence.CreateNewGame(save, mode, pars, flag, GameScenes.SPACECENTER, EditorFacility.None);
                if (game == null) { SpikeS6.Warn("create: CreateNewGame returned null"); return; }
                SpikeS6Host.Instance?.BeginCycle(game, "create");
                game.scenarios.Add(new ProtoScenarioModule(SpikeS6.BuildMarkNode()));
                game.scenarios.Add(new ProtoScenarioModule(SpikeS6.BuildProbeNode()));

                SpikeS6State.Created = new SpikeS6Created
                {
                    Save = save,
                    Choice = SpikeS6.Options.Auto ? SpikeS6.Options.Diff : (diffDone ? (diffAccepted ? SpikeS6DiffChoice.Edit : SpikeS6DiffChoice.Cancel) : SpikeS6DiffChoice.Accept),
                    Accepted = diffDone && diffAccepted,
                    DefaultsRead = defaultsRead,
                    CallbackRead = callbackRead ?? defaultsRead,
                };
                SpikeS6.Info("create save=" + save + " folderLen=" + SpikeS6.Inv(folderText.Length)
                    + " folderEqualsPrimed=" + string.Equals(folderText, SpikeS6.Options.Folder, StringComparison.Ordinal)
                    + " mode=" + mode + " scenarios=" + SpikeS6.Inv(game.scenarios.Count)
                    + " mark=" + SpikeS6.Inv(SpikeS6.CountProtos(game, SpikeS6.MarkName))
                    + " probe=" + SpikeS6.Inv(SpikeS6.CountProtos(game, SpikeS6.ProbeName))
                    + " parametersSameObject=" + ReferenceEquals(game.Parameters, pars)
                    + " saveFolder=" + HighLogic.SaveFolder);
                SpikeS6.Csv("create.csv", new[] { "save", "folderLen", "mode", "scenarios", "diffDone", "accepted" },
                    save, folderText.Length, mode.ToString(), game.scenarios.Count, diffDone, diffAccepted);

                HighLogic.CurrentGame = game;
                GameEvents.onGameNewStart.Fire();
                HighLogic.CurrentGame.Start();
            }
            catch (Exception ex)
            {
                SpikeS6.Warn("create threw " + ex.GetType().Name + ": " + ex.Message);
            }
        }

        // ------------------------------------------------------------ resume (quit -> Resume Saved)

        // MainMenu.OnLoadDialogFinished + OnLoadDialogPipelineFinished, call for call, minus the
        // KSPUpgradePipeline pass (the save was written by this build) and analytics.
        private static void ResumeLikeLoadDialog(SpikeS6Created created)
        {
            created.ResumePending = false;
            created.Resumed = true;
            try
            {
                string folder = created.Save;
                ConfigNode node = GamePersistence.LoadSFSFile("persistent", folder);
                SpikeS6.Info("resume: LoadSFSFile folder=" + folder + " node=" + (node != null)
                    + " nodeMarks=" + SpikeS6.Inv(SpikeS6.CountNodeScenarios(node, SpikeS6.MarkName)));
                if (node == null) return;
                Game game = GamePersistence.LoadGameCfg(node, folder, true, false);
                HighLogic.CurrentGame = game;
                if (game == null) { SpikeS6.Warn("resume: LoadGameCfg returned null"); return; }
                GamePersistence.UpdateScenarioModules(game);
                GamePersistence.SaveGame(game, "persistent", folder, SaveMode.OVERWRITE);
                GameEvents.onGameStatePostLoad.Fire(node);
                HighLogic.SaveFolder = folder;
                SpikeS6.Info("resume: starting mark=" + SpikeS6.Inv(SpikeS6.CountProtos(game, SpikeS6.MarkName)));
                game.Start();
            }
            catch (Exception ex)
            {
                SpikeS6.Warn("resume threw " + ex.GetType().Name + ": " + ex.Message);
            }
        }

        // ------------------------------------------------------------ AUTO

        private IEnumerator AutoDrive()
        {
            SpikeS6.Info("auto: start");
            if (clone == null) { SpikeS6.Warn("auto: no clone, stopping"); yield break; }

            // Stage 0 -> stage 1 through the Start Game button's own handler.
            if (menu.startBtn != null && menu.startBtn.onTap != null) menu.startBtn.onTap();
            for (int i = 0; i < 90; i++) yield return null;

            // Keyboard: the same event MenuNavigation fires for the arrow keys and Enter.
            int steps = 0;
            GameEvents.onMenuNavGetInput.Fire(MenuNavInput.Down); // first input only highlights the first entry
            yield return null;
            while (true)
            {
                var next = SpikeS6Options.NextNav(clone != null && clone.isBeingHovered, steps, SpikeS6Options.NavMaxSteps);
                if (next == SpikeS6NavAction.Accept)
                {
                    SpikeS6.Info("auto: nav reached the clone after Down x" + SpikeS6.Inv(steps) + "; Accept");
                    autoAccepting = true;
                    GameEvents.onMenuNavGetInput.Fire(MenuNavInput.Accept);
                    autoAccepting = false;
                    break;
                }
                if (next == SpikeS6NavAction.GiveUp)
                {
                    SpikeS6.Warn("auto: nav did not reach the clone in " + SpikeS6.Inv(steps) + " steps; tapping it directly");
                    autoAccepting = true;
                    OnMultiplayerTap();
                    autoAccepting = false;
                    break;
                }
                GameEvents.onMenuNavGetInput.Fire(MenuNavInput.Down);
                steps++;
                for (int i = 0; i < 5; i++) yield return null;
            }
            for (int i = 0; i < 30; i++) yield return null;
            if (dialog == null) { SpikeS6.Warn("auto: dialog did not open, stopping"); yield break; }

            bool folderSet = SetInputText(folderInput, SpikeS6.Options.Folder);
            yield return null;
            SpikeS6.Info("auto: folder input set=" + folderSet + " readBackLen=" + SpikeS6.Inv(folderText.Length)
                + " equals=" + string.Equals(folderText, SpikeS6.Options.Folder, StringComparison.Ordinal));

            OpenDifficulty();
            DifficultyOptionsMenu panelMenu = dom;
            PopupDialog panel = null;
            for (int i = 0; i < 180 && panel == null; i++) { yield return null; panel = FindPanel(); }
            if (panelMenu == null || panel == null)
            {
                SpikeS6.Warn("auto: difficulty panel not found (menu=" + (panelMenu != null) + " popup=" + (panel != null) + ")");
            }
            else
            {
                for (int i = 0; i < 30; i++) yield return null;
                ApplyAutoDifficulty(panelMenu);
                for (int i = 0; i < 30 && !diffDone; i++) yield return null;
            }

            for (int i = 0; i < 30; i++) yield return null;
            SpikeS6.Info("auto: Create (difficultyDone=" + diffDone + ")");
            PopupDialog d = dialog;
            Create();
            if (d != null) d.Dismiss();
        }

        private void ApplyAutoDifficulty(DifficultyOptionsMenu panelMenu)
        {
            var choice = SpikeS6.Options.Diff;
            FieldInfo gParsField = AccessTools.Field(typeof(DifficultyOptionsMenu), "gPars");
            var live = gParsField != null ? gParsField.GetValue(panelMenu) as GameParameters : null;
            SpikeS6.Info("auto: panel gPars found=" + (live != null) + " sameAsPassed=" + ReferenceEquals(live, pars)
                + " choice=" + choice);
            if (live != null && choice != SpikeS6DiffChoice.Accept)
            {
                object[] parts = SpikeS6Difficulty.PartsOf(live);
                foreach (var e in SpikeS6Difficulty.PlannedEdits)
                {
                    string reason;
                    bool ok = SpikeS6Difficulty.TrySet(parts[0], parts[1], parts[2], parts[3], e.Key, e.Value, out reason);
                    SpikeS6.Info("auto: edit " + e.Key + "=" + e.Value + " ok=" + ok + (ok ? "" : " reason=" + reason)
                        + " (set on the panel's GameParameters, not through its controls)");
                }
            }
            MethodInfo dismiss = AccessTools.Method(typeof(DifficultyOptionsMenu), "Dismiss", new[] { typeof(bool) });
            if (dismiss == null)
            {
                SpikeS6.Warn("auto: DifficultyOptionsMenu.Dismiss(bool) missing; HAND: press Accept or Cancel on the panel");
                return;
            }
            bool commit = choice != SpikeS6DiffChoice.Cancel;
            SpikeS6.Info("auto: panel Dismiss(" + commit + ")");
            InvokeDismiss(dismiss, panelMenu, commit);
        }

        [MethodImpl(MethodImplOptions.NoInlining)]
        private static void InvokeDismiss(MethodInfo dismiss, DifficultyOptionsMenu target, bool commit)
        {
            try { dismiss.Invoke(target, new object[] { commit }); }
            catch (Exception ex) { SpikeS6.Warn("auto: Dismiss threw " + (ex.InnerException ?? ex).GetType().Name + ": " + (ex.InnerException ?? ex).Message); }
        }
    }
}
