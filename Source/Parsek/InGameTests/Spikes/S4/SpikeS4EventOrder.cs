using System;
using System.Collections;
using System.Collections.Generic;
using System.Globalization;
using System.Reflection;
using System.Runtime.CompilerServices;
using HarmonyLib;
using UnityEngine;

namespace Parsek.InGameTests.Spikes
{
    // R0 spike branch only. Spike S4a: the order of the scene and save events on each way
    // into a save, and (second switch) the Game.Start turn-back of R2-02.
    //
    // Armed by PARSEK_SPIKE=S4a: SpikeRegistry calls Apply once at Startup.Instantly. Apply
    // hand-patches the hooks below through the spike Harmony instance (no [HarmonyPatch]
    // anywhere), each target resolved alone so a missing one is logged and skipped, then
    // creates one DontDestroyOnLoad GameObject carrying this component, which subscribes the
    // GameEvents and runs the KSC dump ~3 s after each SPACECENTER / FLIGHT arrival.
    //
    // The turn-back is armed only when PARSEK_SPIKE_S4A_TURNBACK=1 as well: the Game.Start
    // prefix then refuses a game whose save folder or title starts with SPIKE-REFUSE (logs
    // "turn-back scene=...", posts one screen message, sets HighLogic.CurrentGame to null,
    // skips Game.Start). Every other game starts normally.
    //
    // Every event is one [Spike.S4a] log line and one row of parsek-spike/S4a/event_order.csv.
    internal sealed class SpikeS4EventOrder : MonoBehaviour
    {
        internal static string Tag => SpikeArming.SpikeTag(SpikeS4Format.SpikeId);

        /// <summary>Process session stamp written in every CSV row (UTC, set at Apply).</summary>
        internal static string Session { get; private set; } = "unarmed";

        private static bool turnBackArmed;
        private static int seq;
        private static readonly SpikeObjectIds GameIds = new SpikeObjectIds("g");
        private static SpikeS4EventOrder instance;

        private const float DumpDelaySeconds = 3f;

        // Hooks: (type, method, argument types or null for "the only overload", kind).
        private enum HookKind { Marker, GameStart, SpaceCenterStart, PqsCityOnSetup }

        /// <summary>SpikeRegistry entry: applies the S4a patches and creates the event host.</summary>
        internal static void Apply(Harmony harmony)
        {
            Session = DateTime.UtcNow.ToString("yyyyMMdd'T'HHmmss'Z'", CultureInfo.InvariantCulture);
            turnBackArmed = SpikeS4Format.IsTurnBackArmedValue(
                Environment.GetEnvironmentVariable(SpikeS4Format.TurnBackEnvVar));
            ParsekLog.Info(Tag, "event order armed session=" + Session
                + " turnback=" + (turnBackArmed ? "armed" : "off")
                + " csv=" + SafeCsvDir());

            int ok = 0, missing = 0;
            foreach (var h in Hooks())
            {
                if (TryPatch(harmony, h.Item1, h.Item2, h.Item3, h.Item4)) ok++;
                else missing++;
            }
            ParsekLog.Info(Tag, string.Format(CultureInfo.InvariantCulture,
                "hooks applied={0} missing={1}", ok, missing));

            var go = new GameObject("ParsekSpikeS4aEventOrder");
            DontDestroyOnLoad(go);
            go.AddComponent<SpikeS4EventOrder>();
        }

        private static IEnumerable<Tuple<Type, string, Type[], HookKind>> Hooks()
        {
            yield return T(typeof(Game), "Start", Type.EmptyTypes, HookKind.GameStart);
            yield return T(typeof(SpaceCenter), "Start", Type.EmptyTypes, HookKind.SpaceCenterStart);
            yield return T(typeof(PQSCity), "OnSetup", Type.EmptyTypes, HookKind.PqsCityOnSetup);
            yield return T(typeof(ParsekScenario), "OnLoad", new[] { typeof(ConfigNode) }, HookKind.Marker);
            // Entry points that name the way in (the summary script segments on them).
            yield return T(typeof(MainMenu), "OnLoadDialogPipelineFinished",
                new[] { typeof(ConfigNode), typeof(string) }, HookKind.Marker);
            yield return T(typeof(MainMenu), "CreateAndStartGame",
                new[] { typeof(string), typeof(Game.Modes), typeof(GameScenes), typeof(EditorFacility) }, HookKind.Marker);
            yield return T(typeof(QuickSaveLoad), "quickLoad", new[] { typeof(string), typeof(string) }, HookKind.Marker);
            yield return T(typeof(FlightDriver), "RevertToLaunch", Type.EmptyTypes, HookKind.Marker);
            yield return T(typeof(FlightDriver), "StartWithNewLaunch",
                new[] { typeof(string), typeof(string), typeof(string), typeof(VesselCrewManifest) }, HookKind.Marker);
            yield return T(typeof(FlightDriver), "StartAndFocusVessel", new[] { typeof(Game), typeof(int) }, HookKind.Marker);
            yield return T(typeof(FlightDriver), "StartAndFocusVessel", new[] { typeof(string), typeof(int) }, HookKind.Marker);
            // Save-file reads and writes, and the scene request itself.
            yield return T(typeof(GamePersistence), "LoadGame",
                new[] { typeof(string), typeof(string), typeof(bool), typeof(bool) }, HookKind.Marker);
            yield return T(typeof(GamePersistence), "SaveGame",
                new[] { typeof(Game), typeof(string), typeof(string), typeof(SaveMode) }, HookKind.Marker);
            yield return T(typeof(GamePersistence), "SaveGame",
                new[] { typeof(string), typeof(string), typeof(SaveMode), typeof(GameScenes) }, HookKind.Marker);
            yield return T(typeof(HighLogic), "LoadScene", new[] { typeof(GameScenes) }, HookKind.Marker);
        }

        private static Tuple<Type, string, Type[], HookKind> T(Type t, string m, Type[] args, HookKind k)
            => Tuple.Create(t, m, args, k);

        private static bool TryPatch(Harmony harmony, Type type, string name, Type[] args, HookKind kind)
        {
            string label = type.Name + "." + name + "(" + ArgList(args) + ")";
            try
            {
                MethodInfo target = AccessTools.Method(type, name, args);
                if (target == null)
                {
                    ParsekLog.Warn(Tag, "hook missing: " + label);
                    return false;
                }
                switch (kind)
                {
                    case HookKind.GameStart:
                        harmony.Patch(target, prefix: new HarmonyMethod(Own(nameof(GameStartPrefix))));
                        break;
                    case HookKind.SpaceCenterStart:
                        harmony.Patch(target, postfix: new HarmonyMethod(Own(nameof(SpaceCenterStartPostfix))));
                        break;
                    case HookKind.PqsCityOnSetup:
                        harmony.Patch(target, postfix: new HarmonyMethod(Own(nameof(PqsCityOnSetupPostfix))));
                        break;
                    default:
                        harmony.Patch(target, prefix: new HarmonyMethod(Own(nameof(MarkerPrefix))));
                        break;
                }
                ParsekLog.Info(Tag, "hook applied: " + label + " kind=" + kind);
                return true;
            }
            catch (Exception ex)
            {
                ParsekLog.Warn(Tag, "hook failed: " + label + " " + ex.GetType().Name + ": " + ex.Message);
                return false;
            }
        }

        private static MethodInfo Own(string name)
            => typeof(SpikeS4EventOrder).GetMethod(name, BindingFlags.Static | BindingFlags.NonPublic);

        private static string ArgList(Type[] args)
        {
            if (args == null) return "*";
            var names = new string[args.Length];
            for (int i = 0; i < args.Length; i++) names[i] = args[i].Name;
            return string.Join(",", names);
        }

        // ---- patches (applied by hand; never attribute-driven) ----

        private static void MarkerPrefix(MethodBase __originalMethod, object[] __args)
        {
            try
            {
                var parts = new List<string>();
                if (__args != null)
                    foreach (object a in __args)
                        parts.Add(a is Game g ? "game:" + GameIds.IdOf(g) : SpikeS4Format.FormatArg(a));
                Record(SpikeS4Format.MethodLabel(__originalMethod), string.Join(" ", parts.ToArray()));
            }
            catch (Exception ex)
            {
                ParsekLog.Warn(Tag, "marker failed: " + ex.GetType().Name + ": " + ex.Message);
            }
        }

        private static bool GameStartPrefix(Game __instance)
        {
            string title = null, folder = null, scene = "?";
            try
            {
                title = __instance != null ? __instance.Title : null;
                folder = HighLogic.SaveFolder;
                scene = HighLogic.LoadedScene.ToString();
                Record("Game.Start", "starting:" + GameIds.IdOf(__instance) + " startScene="
                    + (__instance != null ? __instance.startScene.ToString() : "?"));
            }
            catch (Exception ex)
            {
                ParsekLog.Warn(Tag, "Game.Start marker failed: " + ex.GetType().Name + ": " + ex.Message);
            }

            if (!turnBackArmed || !SpikeS4Format.ShouldTurnBack(title, folder))
                return true;

            // R2-02's refused load: stay where we are, show why, drop the game.
            ParsekLog.Info(Tag, "turn-back scene=" + scene + " folder=" + (folder ?? "") + " title=" + (title ?? "")
                + " game=" + GameIds.IdOf(__instance));
            bool shown = ParsekLog.TryScreenMessage("Spike S4a: this save was turned back (" + (folder ?? "") + ")", 10f);
            HighLogic.CurrentGame = null;
            ParsekLog.Info(Tag, "turn-back done message=" + (shown ? "shown" : "dropped")
                + " currentGame=" + (HighLogic.CurrentGame == null ? "null" : "set")
                + " saveFolderAfter=" + (HighLogic.SaveFolder ?? ""));
            Record("turn-back", "folder=" + (folder ?? "") + " message=" + (shown ? "shown" : "dropped"));
            return false;
        }

        private static void SpaceCenterStartPostfix(SpaceCenter __instance)
        {
            try
            {
                Record("SpaceCenter.Start", __instance == null ? "null" : string.Format(CultureInfo.InvariantCulture,
                    "lat={0} lon={1}", SpikeS4Format.D(__instance.Latitude), SpikeS4Format.D(__instance.Longitude)));
            }
            catch (Exception ex)
            {
                ParsekLog.Warn(Tag, "SpaceCenter.Start marker failed: " + ex.GetType().Name + ": " + ex.Message);
            }
        }

        private static void PqsCityOnSetupPostfix(PQSCity __instance)
        {
            try
            {
                string name = __instance != null ? __instance.name : "null";
                Record("PQSCity.OnSetup", "name=" + name + (__instance != null
                    ? string.Format(CultureInfo.InvariantCulture, " lat={0} lon={1} alt={2}",
                        SpikeS4Format.D(__instance.lat), SpikeS4Format.D(__instance.lon), SpikeS4Format.D(__instance.alt))
                    : ""));
            }
            catch (Exception ex)
            {
                ParsekLog.Warn(Tag, "PQSCity.OnSetup marker failed: " + ex.GetType().Name + ": " + ex.Message);
            }
        }

        // ---- GameEvents ----

        void Awake()
        {
            if (instance != null) { Destroy(gameObject); return; }
            instance = this;
            GameEvents.onGameSceneLoadRequested.Add(OnSceneLoadRequested);
            GameEvents.onGameSceneSwitchRequested.Add(OnSceneSwitchRequested);
            GameEvents.onGameStateCreated.Add(OnGameStateCreated);
            GameEvents.onGameStateLoad.Add(OnGameStateLoad);
            GameEvents.onGameStatePostLoad.Add(OnGameStatePostLoad);
            GameEvents.onGameNewStart.Add(OnGameNewStart);
            GameEvents.onLevelWasLoaded.Add(OnLevelWasLoaded);
            GameEvents.onLevelWasLoadedGUIReady.Add(OnLevelWasLoadedGUIReady);
            GameEvents.onFlightReady.Add(OnFlightReady);
            Record("spike.awake", "subscribed");
        }

        void OnDestroy()
        {
            if (instance != this) return;
            GameEvents.onGameSceneLoadRequested.Remove(OnSceneLoadRequested);
            GameEvents.onGameSceneSwitchRequested.Remove(OnSceneSwitchRequested);
            GameEvents.onGameStateCreated.Remove(OnGameStateCreated);
            GameEvents.onGameStateLoad.Remove(OnGameStateLoad);
            GameEvents.onGameStatePostLoad.Remove(OnGameStatePostLoad);
            GameEvents.onGameNewStart.Remove(OnGameNewStart);
            GameEvents.onLevelWasLoaded.Remove(OnLevelWasLoaded);
            GameEvents.onLevelWasLoadedGUIReady.Remove(OnLevelWasLoadedGUIReady);
            GameEvents.onFlightReady.Remove(OnFlightReady);
            instance = null;
        }

        private void OnSceneLoadRequested(GameScenes s) => Record("onGameSceneLoadRequested", "to=" + s);
        private void OnSceneSwitchRequested(GameEvents.FromToAction<GameScenes, GameScenes> a)
            => Record("onGameSceneSwitchRequested", "from=" + a.from + " to=" + a.to);
        private void OnGameStateCreated(Game g) => Record("onGameStateCreated", "game:" + GameIds.IdOf(g));
        private void OnGameStateLoad(ConfigNode n) => Record("onGameStateLoad", n != null ? "node" : "null");
        private void OnGameStatePostLoad(ConfigNode n) => Record("onGameStatePostLoad", n != null ? "node" : "null");
        private void OnGameNewStart() => Record("onGameNewStart", "");
        private void OnLevelWasLoaded(GameScenes s) => Record("onLevelWasLoaded", "scene=" + s);

        private void OnLevelWasLoadedGUIReady(GameScenes s)
        {
            Record("onLevelWasLoadedGUIReady", "scene=" + s);
            if (s == GameScenes.SPACECENTER)
                StartCoroutine(DumpAfterDelay("auto-spacecenter"));
        }

        private void OnFlightReady()
        {
            Record("onFlightReady", "");
            StartCoroutine(DumpAfterDelay("auto-flight"));
        }

        private IEnumerator DumpAfterDelay(string reason)
        {
            GameScenes scene = HighLogic.LoadedScene;
            yield return new WaitForSecondsRealtime(DumpDelaySeconds);
            if (HighLogic.LoadedScene != scene)
            {
                ParsekLog.Info(Tag, "auto dump skipped: scene changed from " + scene + " to " + HighLogic.LoadedScene);
                yield break;
            }
            try
            {
                SpikeS4Probe.Dump(Session, reason);
            }
            catch (Exception ex)
            {
                ParsekLog.Warn(Tag, "auto dump failed: " + ex.GetType().Name + ": " + ex.Message);
            }
        }

        // ---- one event = one log line + one CSV row ----

        [MethodImpl(MethodImplOptions.NoInlining)]
        private static void Record(string evt, string detail)
        {
            seq++;
            int frame = -1;
            double realtime = -1;
            string scene = "?", gameId = "none", title = "", folder = "";
            try
            {
                frame = Time.frameCount;
                realtime = Time.realtimeSinceStartup;
                scene = HighLogic.LoadedScene.ToString();
                folder = HighLogic.SaveFolder ?? "";
                Game g = HighLogic.CurrentGame;
                gameId = GameIds.IdOf(g);
                title = g != null ? g.Title ?? "" : "";
            }
            catch (Exception)
            {
                // HighLogic is not up yet at Startup.Instantly; the row keeps the defaults.
            }
            ParsekLog.Info(Tag, SpikeS4Format.FormatEventMessage(seq, frame, evt, detail, scene, gameId, title, folder));
            try
            {
                SpikeCsv.AppendRow(SpikeS4Format.SpikeId, "event_order", SpikeS4Format.EventHeader,
                    SpikeS4Format.EventRow(Session, seq, frame, realtime, evt, detail, scene, gameId, title, folder));
            }
            catch (Exception ex)
            {
                ParsekLog.Warn(Tag, "event csv write failed: " + ex.GetType().Name + ": " + ex.Message);
            }
        }

        [MethodImpl(MethodImplOptions.NoInlining)]
        private static string SafeCsvDir()
        {
            try { return SpikeCsv.PathFor(SpikeS4Format.SpikeId, "event_order"); }
            catch (Exception ex) { return "<unresolved: " + ex.Message + ">"; }
        }
    }
}
