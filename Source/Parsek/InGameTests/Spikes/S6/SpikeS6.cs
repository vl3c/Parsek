using System;
using System.Collections.Generic;
using System.Globalization;
using System.Reflection;
using HarmonyLib;
using UnityEngine;

namespace Parsek.InGameTests.Spikes.S6
{
    // R0 spike branch only (spike/mp-r0, never merged). Plan: r0-spikes.md, task S6.
    //
    // Entry point registered in SpikeRegistry.All. Apply runs only when PARSEK_SPIKE names S6
    // (SpikeHost's Awake at Startup.Instantly); it patches by hand (no [HarmonyPatch] on any
    // type here) and creates the S6 host object. Unarmed, nothing in this folder runs: the
    // probe ScenarioModule is the one type KSP itself sees, and it is never added to a game
    // (ScenarioCreationOptions.None) and is silent unless armed.
    internal static class SpikeS6
    {
        internal const string Id = "S6";
        internal const string MarkName = "ParsekRaceSpikeMark";
        internal const string ProbeName = "SpikeS6ProbeScenario";
        internal const string MarkScenes = "5, 7, 8";

        internal static readonly string Tag = SpikeArming.SpikeTag(Id);
        internal static SpikeS6Options Options = new SpikeS6Options();

        private static int patchesApplied;
        private static int patchesMissing;

        internal static readonly string[] HookCsvHeader =
        {
            "seq", "cycle", "hook", "scene", "arg", "game", "cycleGame", "saveFolder",
            "markInGame", "probeProto", "probeLoaded", "nodeMarks",
        };

        internal static readonly string[] SaveCsvHeader =
        {
            "seq", "trigger", "file", "bytes", "isGame", "marks", "hash", "probes", "scenarios", "snapshot",
        };

        internal static readonly string[] DifficultyCsvHeader = { "phase", "key", "value" };

        internal static void Info(string msg) => ParsekLog.Info(Tag, msg);
        internal static void Warn(string msg) => ParsekLog.Warn(Tag, msg);

        internal static string Inv(int v) => v.ToString(CultureInfo.InvariantCulture);
        internal static string Inv(long v) => v.ToString(CultureInfo.InvariantCulture);
        internal static string Inv(double v) => v.ToString("R", CultureInfo.InvariantCulture);

        /// <summary>Appends one CSV row; a failed write is one warn line, never an exception.</summary>
        internal static void Csv(string name, string[] header, params object[] fields)
        {
            try { SpikeCsv.AppendRow(Id, name, header, fields); }
            catch (Exception ex) { Warn("csv write failed name=" + name + ": " + ex.GetType().Name + ": " + ex.Message); }
        }

        /// <summary>The mark node exactly as the template carries it.</summary>
        internal static ConfigNode BuildMarkNode()
        {
            var node = new ConfigNode("SCENARIO");
            node.AddValue("name", MarkName);
            node.AddValue("scene", MarkScenes);
            var race = node.AddNode("race");
            race.AddValue("id", "spike-1");
            race.AddValue("slot", "2");
            return node;
        }

        /// <summary>A proto of a module type that IS installed, added the same way as the mark.</summary>
        internal static ConfigNode BuildProbeNode()
        {
            var node = new ConfigNode("SCENARIO");
            node.AddValue("name", ProbeName);
            node.AddValue("scene", "5, 6, 7, 8");
            node.AddValue("marker", "spike-s6-probe");
            return node;
        }

        internal static int CountProtos(Game game, string moduleName)
        {
            if (game == null || game.scenarios == null) return -1;
            int n = 0;
            foreach (var p in game.scenarios)
                if (p != null && string.Equals(p.moduleName, moduleName, StringComparison.Ordinal)) n++;
            return n;
        }

        /// <summary>SCENARIO children named <paramref name="moduleName"/>, on the node or its GAME child.</summary>
        internal static int CountNodeScenarios(ConfigNode node, string moduleName)
        {
            if (node == null) return -1;
            ConfigNode game = node.HasNode("GAME") ? node.GetNode("GAME") : node;
            int n = 0;
            foreach (ConfigNode s in game.GetNodes("SCENARIO"))
                if (s.GetValue("name") == moduleName) n++;
            return n;
        }

        internal static int ProbeLoaded()
        {
            try
            {
                if (ScenarioRunner.Instance == null) return -1;
                int n = 0;
                foreach (var m in ScenarioRunner.GetLoadedModules())
                    if (m is SpikeS6ProbeScenario) n++;
                return n;
            }
            catch (Exception)
            {
                return -1;
            }
        }

        // ---------------------------------------------------------------- Apply

        internal static void Apply(Harmony harmony)
        {
            Options = SpikeS6Options.Parse(Environment.GetEnvironmentVariable);
            Info("options " + Options.Describe());

            Type gp = typeof(GamePersistence);
            Patch(harmony, "GamePersistence.SaveGame(Game,string,string,SaveMode)",
                AccessTools.Method(gp, "SaveGame", new[] { typeof(Game), typeof(string), typeof(string), typeof(SaveMode) }),
                null, nameof(SaveGamePostfix));
            Patch(harmony, "GamePersistence.SaveGame(GameBackup,string,string,SaveMode)",
                AccessTools.Method(gp, "SaveGame", new[] { typeof(GameBackup), typeof(string), typeof(string), typeof(SaveMode) }),
                null, nameof(SaveGamePostfix));
            Patch(harmony, "GamePersistence.LoadGameCfg(ConfigNode,string,bool,bool)",
                AccessTools.Method(gp, "LoadGameCfg", new[] { typeof(ConfigNode), typeof(string), typeof(bool), typeof(bool) }),
                null, nameof(LoadGameCfgPostfix));
            Patch(harmony, "Game.Start()",
                AccessTools.Method(typeof(Game), "Start", Type.EmptyTypes),
                nameof(GameStartPrefix), null);
            Patch(harmony, "ScenarioRunner.LoadModules(List<ProtoScenarioModule>)",
                AccessTools.Method(typeof(ScenarioRunner), "LoadModules", new[] { typeof(List<ProtoScenarioModule>) }),
                nameof(LoadModulesPrefix), null);
            Patch(harmony, "ProtoScenarioModule.Load(ScenarioRunner)",
                AccessTools.Method(typeof(ProtoScenarioModule), "Load", new[] { typeof(ScenarioRunner) }),
                null, nameof(ProtoLoadPostfix));

            Info("patch summary applied=" + Inv(patchesApplied) + " missing=" + Inv(patchesMissing));

            var go = new GameObject("ParsekSpikeS6Host");
            UnityEngine.Object.DontDestroyOnLoad(go);
            go.AddComponent<SpikeS6Host>();
        }

        private static void Patch(Harmony harmony, string label, MethodInfo target, string prefix, string postfix)
        {
            if (target == null)
            {
                patchesMissing++;
                Warn("patch target missing: " + label + " (continuing without it)");
                return;
            }
            try
            {
                harmony.Patch(target,
                    prefix: prefix == null ? null : new HarmonyMethod(typeof(SpikeS6), prefix),
                    postfix: postfix == null ? null : new HarmonyMethod(typeof(SpikeS6), postfix));
                patchesApplied++;
                Info("patched " + label);
            }
            catch (Exception ex)
            {
                patchesMissing++;
                Warn("patch failed: " + label + ": " + ex.GetType().Name + ": " + ex.Message);
            }
        }

        // ---------------------------------------------------------------- patches

        private static void SaveGamePostfix(string saveFileName, string saveFolder, SaveMode saveMode, string __result)
        {
            try
            {
                Info("savegame-postfix file=" + saveFileName + " folder=" + saveFolder + " mode=" + saveMode
                    + " result=" + (__result ?? "<null>") + " scene=" + HighLogic.LoadedScene);
                SpikeS6Host.Instance?.OnSaveGameReturned(saveFolder);
            }
            catch (Exception ex)
            {
                Warn("savegame-postfix threw " + ex.GetType().Name + ": " + ex.Message);
            }
        }

        private static void LoadGameCfgPostfix(ConfigNode node, string saveName, Game __result)
        {
            try
            {
                SpikeS6Host.Instance?.BeginCycle(__result, "LoadGameCfg");
                SpikeS6Host.Instance?.LogHook("LoadGameCfg-postfix", saveName,
                    nodeMarks: CountNodeScenarios(node, MarkName),
                    note: "resultMarks=" + Inv(CountProtos(__result, MarkName)));
            }
            catch (Exception ex)
            {
                Warn("loadgamecfg-postfix threw " + ex.GetType().Name + ": " + ex.Message);
            }
        }

        private static void GameStartPrefix(Game __instance)
        {
            try
            {
                SpikeS6Host.Instance?.LogHook("Game.Start-prefix", __instance == HighLogic.CurrentGame ? "current" : "other",
                    note: "instanceMarks=" + Inv(CountProtos(__instance, MarkName))
                        + " instanceProbe=" + Inv(CountProtos(__instance, ProbeName)));
            }
            catch (Exception ex)
            {
                Warn("game-start-prefix threw " + ex.GetType().Name + ": " + ex.Message);
            }
        }

        private static void LoadModulesPrefix(List<ProtoScenarioModule> protoModules)
        {
            try
            {
                int marks = 0, probes = 0;
                if (protoModules != null)
                    foreach (var p in protoModules)
                    {
                        if (p == null) continue;
                        if (p.moduleName == MarkName) marks++;
                        if (p.moduleName == ProbeName) probes++;
                    }
                SpikeS6Host.Instance?.LogHook("ScenarioRunner.LoadModules-prefix",
                    "protos=" + Inv(protoModules == null ? -1 : protoModules.Count),
                    note: "listMarks=" + Inv(marks) + " listProbes=" + Inv(probes));
            }
            catch (Exception ex)
            {
                Warn("loadmodules-prefix threw " + ex.GetType().Name + ": " + ex.Message);
            }
        }

        private static void ProtoLoadPostfix(ProtoScenarioModule __instance, ScenarioModule __result)
        {
            try
            {
                if (__instance == null) return;
                if (__instance.moduleName != MarkName && __instance.moduleName != ProbeName) return;
                Info("proto-load module=" + __instance.moduleName + " scene=" + HighLogic.LoadedScene
                    + " instantiated=" + (__result != null) + " moduleRef=" + (__instance.moduleRef != null));
            }
            catch (Exception ex)
            {
                Warn("proto-load-postfix threw " + ex.GetType().Name + ": " + ex.Message);
            }
        }
    }
}
