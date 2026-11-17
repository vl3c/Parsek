using System;
using System.Collections;
using System.Collections.Generic;
using System.IO;
using System.Reflection;
using UnityEngine;

namespace Parsek.InGameTests.Spikes.S6
{
    // R0 spike branch only (spike/mp-r0, never merged). Plan: r0-spikes.md, task S6.
    //
    // Created by SpikeS6.Apply only (armed). Three jobs:
    //  1. Hook order: one [Spike.S6] hook line + hooks.csv row per GameEvents hook and per
    //     Harmony patch point, with the mark's presence in HighLogic.CurrentGame.scenarios;
    //     "earliest" names the first hook of a load or create cycle where the cycle's own Game
    //     is current and holds the mark.
    //  2. Save watch: every .sfs under the watched save folders (persistent, quicksave,
    //     rewind points, Parsek's own copies) is re-read when it changes and settles, and its
    //     mark facts logged (savewatch line + savewatch.csv); a copy goes to snapshots/.
    //  3. The menu and Space Center steps: adds SpikeS6Menu at the main menu, reads the
    //     created game's difficulty and the R&D tree at the Space Center, runs the resume.
    internal sealed class SpikeS6Host : MonoBehaviour
    {
        internal static SpikeS6Host Instance;

        private int hookSeq;
        private int saveSeq;
        private int cycle;
        private Game cycleGame;
        private string cycleSource = "none";
        private bool cycleEarliestLogged;

        private readonly HashSet<string> watchedFolders = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        private readonly Dictionary<string, string> lastStamp = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        private readonly Dictionary<string, string> pendingStamp = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        private readonly HashSet<string> scannedFolders = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        private float nextPoll;
        private const float PollSeconds = 1.0f;

        private int scArrivals;

        void Awake()
        {
            Instance = this;
            GameEvents.onGameSceneLoadRequested.Add(OnSceneLoadRequested);
            GameEvents.onLevelWasLoaded.Add(OnLevelLoaded);
            GameEvents.onLevelWasLoadedGUIReady.Add(OnLevelGuiReady);
            GameEvents.onGameStateCreated.Add(OnGameStateCreated);
            GameEvents.onGameStateLoad.Add(OnGameStateLoad);
            GameEvents.onGameStatePostLoad.Add(OnGameStatePostLoad);
            GameEvents.onGameStateSave.Add(OnGameStateSave);
            GameEvents.onGameStateSaved.Add(OnGameStateSaved);
            GameEvents.onGameNewStart.Add(OnGameNewStart);
            GameEvents.onGUIRnDComplexSpawn.Add(OnRnDSpawn);
            gameObject.AddComponent<SpikeS6Menu>(); // acts each time a MainMenu instance appears
            SpikeS6.Info("host awake: hooks subscribed, save watch every " + SpikeS6.Inv(PollSeconds) + "s");
        }

        void OnDestroy()
        {
            GameEvents.onGameSceneLoadRequested.Remove(OnSceneLoadRequested);
            GameEvents.onLevelWasLoaded.Remove(OnLevelLoaded);
            GameEvents.onLevelWasLoadedGUIReady.Remove(OnLevelGuiReady);
            GameEvents.onGameStateCreated.Remove(OnGameStateCreated);
            GameEvents.onGameStateLoad.Remove(OnGameStateLoad);
            GameEvents.onGameStatePostLoad.Remove(OnGameStatePostLoad);
            GameEvents.onGameStateSave.Remove(OnGameStateSave);
            GameEvents.onGameStateSaved.Remove(OnGameStateSaved);
            GameEvents.onGameNewStart.Remove(OnGameNewStart);
            GameEvents.onGUIRnDComplexSpawn.Remove(OnRnDSpawn);
            if (Instance == this) Instance = null;
        }

        // ------------------------------------------------------------ hooks

        internal void BeginCycle(Game game, string source)
        {
            cycle++;
            cycleGame = game;
            cycleSource = source;
            cycleEarliestLogged = false;
            SpikeS6.Info("cycle begin cycle=" + SpikeS6.Inv(cycle) + " source=" + source
                + " gameMarks=" + SpikeS6.Inv(SpikeS6.CountProtos(game, SpikeS6.MarkName)));
        }

        internal void LogHook(string hook, string arg, int nodeMarks = -1, string note = null)
        {
            hookSeq++;
            Game game = HighLogic.CurrentGame;
            bool isCycleGame = game != null && ReferenceEquals(game, cycleGame);
            int marks = SpikeS6.CountProtos(game, SpikeS6.MarkName);
            int probeProto = SpikeS6.CountProtos(game, SpikeS6.ProbeName);
            int probeLoaded = SpikeS6.ProbeLoaded();
            string folder = HighLogic.SaveFolder ?? "";
            SpikeS6.Info("hook seq=" + SpikeS6.Inv(hookSeq) + " cycle=" + SpikeS6.Inv(cycle) + " name=" + hook
                + " scene=" + HighLogic.LoadedScene + " arg=" + (arg ?? "") + " game=" + (game != null ? 1 : 0)
                + " cycleGame=" + (isCycleGame ? 1 : 0) + " folder=" + folder + " mark=" + SpikeS6.Inv(marks)
                + " probeProto=" + SpikeS6.Inv(probeProto) + " probeLoaded=" + SpikeS6.Inv(probeLoaded)
                + " nodeMarks=" + SpikeS6.Inv(nodeMarks) + (note != null ? " " + note : ""));
            SpikeS6.Csv("hooks.csv", SpikeS6.HookCsvHeader, hookSeq, cycle, hook, HighLogic.LoadedScene.ToString(),
                arg ?? "", game != null ? 1 : 0, isCycleGame ? 1 : 0, folder, marks, probeProto, probeLoaded, nodeMarks);
            if (isCycleGame && marks > 0 && !cycleEarliestLogged)
            {
                cycleEarliestLogged = true;
                SpikeS6.Info("earliest cycle=" + SpikeS6.Inv(cycle) + " source=" + cycleSource + " hook=" + hook
                    + " scene=" + HighLogic.LoadedScene + " seq=" + SpikeS6.Inv(hookSeq));
            }
            if (!string.IsNullOrEmpty(folder)) watchedFolders.Add(folder);
        }

        private void OnSceneLoadRequested(GameScenes to) => LogHook("onGameSceneLoadRequested", to.ToString());

        private void OnLevelLoaded(GameScenes scene) => LogHook("onLevelWasLoaded", scene.ToString());

        private void OnLevelGuiReady(GameScenes scene)
        {
            LogHook("onLevelWasLoadedGUIReady", scene.ToString());
            if (scene == GameScenes.SPACECENTER) StartCoroutine(SpaceCenterArrival());
        }

        private void OnGameStateCreated(Game g) =>
            LogHook("onGameStateCreated", "argMarks=" + SpikeS6.Inv(SpikeS6.CountProtos(g, SpikeS6.MarkName)));

        private void OnGameStateLoad(ConfigNode n) =>
            LogHook("onGameStateLoad", "", SpikeS6.CountNodeScenarios(n, SpikeS6.MarkName));

        private void OnGameStatePostLoad(ConfigNode n) =>
            LogHook("onGameStatePostLoad", "", SpikeS6.CountNodeScenarios(n, SpikeS6.MarkName));

        private void OnGameStateSave(ConfigNode n) =>
            LogHook("onGameStateSave", "", SpikeS6.CountNodeScenarios(n, SpikeS6.MarkName));

        private void OnGameStateSaved(Game g) =>
            LogHook("onGameStateSaved", "argMarks=" + SpikeS6.Inv(SpikeS6.CountProtos(g, SpikeS6.MarkName)));

        private void OnGameNewStart() => LogHook("onGameNewStart", "");

        private void OnRnDSpawn()
        {
            LogHook("onGUIRnDComplexSpawn", "");
            StartCoroutine(ReadRnDNextFrame());
        }

        private IEnumerator ReadRnDNextFrame()
        {
            for (int i = 0; i < 30; i++) yield return null; // the R&D screen builds its tree after the event
            SpikeS6ContentDump.ReadRnD("rnd-open");
        }

        // ------------------------------------------------------------ save watch

        void Update()
        {
            if (Time.realtimeSinceStartup < nextPoll) return;
            nextPoll = Time.realtimeSinceStartup + PollSeconds;
            string folder = HighLogic.SaveFolder;
            if (!string.IsNullOrEmpty(folder)) watchedFolders.Add(folder);
            Scan("poll");
        }

        internal void OnSaveGameReturned(string saveFolder)
        {
            if (!string.IsNullOrEmpty(saveFolder)) watchedFolders.Add(saveFolder);
            Scan("savegame");
        }

        private void Scan(string trigger)
        {
            string savesRoot = Path.Combine(KSPUtil.ApplicationRootPath, "saves");
            foreach (string folder in new List<string>(watchedFolders))
            {
                string dir = Path.Combine(savesRoot, folder);
                if (!Directory.Exists(dir)) continue;
                bool firstScan = !scannedFolders.Contains(folder);
                string[] files;
                try { files = Directory.GetFiles(dir, "*.sfs", SearchOption.AllDirectories); }
                catch (Exception ex)
                {
                    SpikeS6.Warn("savewatch scan failed dir=" + dir + ": " + ex.GetType().Name);
                    continue;
                }
                foreach (string file in files)
                {
                    string stamp;
                    try
                    {
                        var fi = new FileInfo(file);
                        stamp = SpikeS6.Inv(fi.Length) + "@" + fi.LastWriteTimeUtc.Ticks;
                    }
                    catch (Exception) { continue; }
                    string last;
                    if (lastStamp.TryGetValue(file, out last) && last == stamp) { pendingStamp.Remove(file); continue; }
                    // The first scan of a folder reads what is already there ("initial"). After
                    // that a poll reads a file only once it is unchanged across two polls (a
                    // writer may be mid-write); the SaveGame postfix reads at once.
                    bool initial = firstScan;
                    string pend;
                    bool settled = initial || trigger == "savegame"
                        || (pendingStamp.TryGetValue(file, out pend) && pend == stamp);
                    if (!settled) { pendingStamp[file] = stamp; continue; }
                    pendingStamp.Remove(file);
                    lastStamp[file] = stamp;
                    Record(savesRoot, file, initial && trigger != "savegame" ? "initial" : trigger);
                }
                scannedFolders.Add(folder);
            }
        }

        private void Record(string savesRoot, string file, string trigger)
        {
            string text;
            try
            {
                using (var fs = new FileStream(file, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete))
                using (var sr = new StreamReader(fs))
                    text = sr.ReadToEnd();
            }
            catch (Exception ex)
            {
                SpikeS6.Warn("savewatch read failed file=" + file + ": " + ex.GetType().Name);
                return;
            }
            saveSeq++;
            MarkFacts facts = SpikeS6Cfg.ReadMarkFacts(text, SpikeS6.MarkName, SpikeS6.ProbeName);
            string rel = file.Length > savesRoot.Length ? file.Substring(savesRoot.Length).TrimStart('/', '\\').Replace('\\', '/') : file;
            string snapshot = "-";
            if (SpikeS6.Options.Snapshots)
            {
                try
                {
                    string snapDir = Path.Combine(Path.Combine(SpikeCsv.ResolveSpikeRoot(), SpikeS6.Id), "snapshots");
                    Directory.CreateDirectory(snapDir);
                    snapshot = saveSeq.ToString("D4", System.Globalization.CultureInfo.InvariantCulture) + "_" + rel.Replace('/', '~');
                    File.WriteAllText(Path.Combine(snapDir, snapshot), text);
                }
                catch (Exception ex)
                {
                    snapshot = "failed:" + ex.GetType().Name;
                }
            }
            SpikeS6.Info("savewatch seq=" + SpikeS6.Inv(saveSeq) + " trigger=" + trigger + " file=" + rel
                + " bytes=" + SpikeS6.Inv(text.Length) + " game=" + (facts.IsGameSave ? 1 : 0)
                + " marks=" + SpikeS6.Inv(facts.MarkCount) + " hash=" + facts.Hash
                + " probes=" + SpikeS6.Inv(facts.ProbeCount) + " scenarios=" + SpikeS6.Inv(facts.ScenarioCount)
                + " snapshot=" + snapshot);
            SpikeS6.Csv("savewatch.csv", SpikeS6.SaveCsvHeader, saveSeq, trigger, rel, text.Length,
                facts.IsGameSave ? 1 : 0, facts.MarkCount, facts.Hash, facts.ProbeCount, facts.ScenarioCount, snapshot);
        }

        // ------------------------------------------------------------ Space Center

        private IEnumerator SpaceCenterArrival()
        {
            scArrivals++;
            for (int i = 0; i < 30; i++) yield return null;
            Game game = HighLogic.CurrentGame;
            SpikeS6.Info("sc-arrival n=" + SpikeS6.Inv(scArrivals) + " folder=" + HighLogic.SaveFolder
                + " mark=" + SpikeS6.Inv(SpikeS6.CountProtos(game, SpikeS6.MarkName))
                + " probeLoaded=" + SpikeS6.Inv(SpikeS6.ProbeLoaded())
                + " created=" + (SpikeS6State.Created != null ? SpikeS6State.Created.Save : "-"));

            var created = SpikeS6State.Created;
            if (created == null || !string.Equals(created.Save, HighLogic.SaveFolder, StringComparison.OrdinalIgnoreCase))
                yield break; // a lane or hand load of another save: hooks and save watch only

            var read = SpikeS6Difficulty.ReadLive(game != null ? game.Parameters : null);
            created.ScVisits++;
            string phase = created.ScVisits == 1 ? "sc-created" : created.Resumed ? "sc-after-resume" : "sc-visit-" + SpikeS6.Inv(created.ScVisits);
            SpikeS6Menu.LogDifficulty(phase, read);
            var vsCallback = SpikeS6Difficulty.Diff(created.CallbackRead, read);
            SpikeS6.Info("difficulty " + phase + " vs-callback changed=" + SpikeS6.Inv(vsCallback.Count)
                + (vsCallback.Count > 0 ? " first=" + vsCallback[0] : ""));
            if (created.Choice == SpikeS6DiffChoice.Edit && created.Accepted)
            {
                var problems = SpikeS6Difficulty.CheckEdits(created.DefaultsRead, read, SpikeS6Difficulty.PlannedEdits);
                SpikeS6.Info("difficulty check phase=" + phase + " planned=4 problems=" + SpikeS6.Inv(problems.Count)
                    + (problems.Count > 0 ? " [" + string.Join("; ", problems.ToArray()) + "]" : "") + " result="
                    + (problems.Count == 0 ? "PASS" : "FAIL"));
            }
            else
            {
                var moved = SpikeS6Difficulty.Diff(created.DefaultsRead, read);
                SpikeS6.Info("difficulty check phase=" + phase + " choice=" + created.Choice + " accepted=" + created.Accepted
                    + " movedFromDefaults=" + SpikeS6.Inv(moved.Count) + " result=" + (moved.Count == 0 ? "PASS" : "FAIL")
                    + (moved.Count > 0 ? " [" + string.Join("; ", moved.ToArray()) + "]" : ""));
            }

            if (!SpikeS6.Options.Auto) yield break;

            if (!created.Resumed && SpikeS6.Options.Resume)
            {
                // The "quit -> Resume Saved" leg: save, back to the main menu, and the menu
                // resumes through stock's load-dialog sequence (SpikeS6Menu.ResumeLikeLoadDialog).
                string path = GamePersistence.SaveGame("persistent", HighLogic.SaveFolder, SaveMode.OVERWRITE);
                SpikeS6.Info("resume leg: saved persistent path=" + (path ?? "<null>") + ", loading MAINMENU");
                created.ResumePending = true;
                yield return null;
                HighLogic.LoadScene(GameScenes.MAINMENU);
                yield break;
            }

            if (SpikeS6.Options.Content)
            {
                SpikeS6ContentDump.ReadRnD("sc-entry");
                OpenRnDBuilding();
                for (int i = 0; i < 120; i++) yield return null;
            }

            if (SpikeS6.Options.Quit)
            {
                SpikeS6.Info("auto flow complete; quitting (PARSEK_SPIKE_S6_QUIT=1)");
                yield return null;
                Application.Quit();
            }
            else
            {
                SpikeS6.Info("auto flow complete; game left running");
            }
        }

        // The stock click handler of the R&D building (RnDBuilding.OnClicked, protected): it
        // fires onGUIRnDComplexSpawn, whose handler reads the tree the building shows.
        private static void OpenRnDBuilding()
        {
            try
            {
                var rnd = FindObjectOfType<RnDBuilding>();
                MethodInfo click = typeof(RnDBuilding).GetMethod("OnClicked", BindingFlags.Instance | BindingFlags.NonPublic | BindingFlags.Public);
                if (rnd == null || click == null)
                {
                    SpikeS6.Warn("rnd open skipped: building=" + (rnd != null) + " OnClicked=" + (click != null)
                        + " (HAND: click the R&D building)");
                    return;
                }
                click.Invoke(rnd, null);
                SpikeS6.Info("rnd open: RnDBuilding.OnClicked invoked");
            }
            catch (Exception ex)
            {
                SpikeS6.Warn("rnd open failed: " + ex.GetType().Name + ": " + (ex.InnerException ?? ex).Message);
            }
        }
    }

    /// <summary>What the menu created, carried to the Space Center steps.</summary>
    internal sealed class SpikeS6Created
    {
        internal string Save;
        internal SpikeS6DiffChoice Choice;
        internal bool Accepted;
        internal List<KeyValuePair<string, string>> DefaultsRead;
        internal List<KeyValuePair<string, string>> CallbackRead;
        internal bool ResumePending;
        internal bool Resumed;
        internal int ScVisits;
    }

    internal static class SpikeS6State
    {
        internal static SpikeS6Created Created;
    }
}
