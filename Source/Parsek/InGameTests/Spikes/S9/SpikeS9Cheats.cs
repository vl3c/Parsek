using System;
using System.Collections.Generic;
using System.Globalization;
using System.Reflection;
using System.Runtime.CompilerServices;
using HarmonyLib;
using KSP.UI.Screens.DebugToolbar.Screens.Cheats;
using UnityEngine;

namespace Parsek.InGameTests.Spikes
{
    // R0 spike branch only. Spike S9: which stock debug actions a mod can observe.
    //
    // Armed by PARSEK_SPIKE=S9: SpikeRegistry calls Apply once at Startup.Instantly. Apply
    // hand-patches every hook of SpikeS9Hooks through the spike Harmony instance (no
    // [HarmonyPatch] anywhere; each target resolved alone, a miss logged and skipped), then
    // creates one DontDestroyOnLoad GameObject carrying this component, which subscribes the
    // GameEvents a debug action might fire and polls the debug switches once a second.
    //
    // Observation only: every prefix logs and returns; nothing stock is skipped or changed.
    // Output: [Spike.S9] lines in KSP.log and parsek-spike/S9/observed.csv.
    internal sealed class SpikeS9Cheats : MonoBehaviour
    {
        internal static string Tag => SpikeArming.SpikeTag(SpikeS9Format.SpikeId);

        /// <summary>Process session stamp written in every CSV row (UTC, set at Apply).</summary>
        internal static string Session { get; private set; } = "unarmed";

        private static long seq;
        private static readonly SpikeS9Scope Scope = new SpikeS9Scope();
        private static readonly Dictionary<MethodBase, SpikeS9Hook> HookByMethod = new Dictionary<MethodBase, SpikeS9Hook>();
        private static SpikeS9Cheats instance;

        private const float PollPeriodSeconds = 1f;
        private float nextPoll;
        private Dictionary<string, string> lastSnapshot;
        private object lastGame;
        private string lastScene;
        private string lastInventoryScene;

        // ------------------------------------------------------------------ apply

        /// <summary>SpikeRegistry entry: applies the S9 hooks and creates the poll / event host.</summary>
        internal static void Apply(Harmony harmony)
        {
            Session = DateTime.UtcNow.ToString("yyyyMMdd'T'HHmmss'Z'", CultureInfo.InvariantCulture);
            ParsekLog.Info(Tag, "cheat observer armed session=" + Session + " csv=" + SafeCsvPath());

            int ok = 0, missing = 0, failed = 0;
            foreach (SpikeS9Hook hook in SpikeS9Hooks.All())
            {
                switch (TryPatch(harmony, hook))
                {
                    case 0: ok++; break;
                    case 1: missing++; break;
                    default: failed++; break;
                }
            }
            ParsekLog.Info(Tag, string.Format(CultureInfo.InvariantCulture,
                "hooks applied={0} missing={1} failed={2}", ok, missing, failed));

            var go = new GameObject("ParsekSpikeS9Cheats");
            DontDestroyOnLoad(go);
            go.AddComponent<SpikeS9Cheats>();
        }

        /// <summary>0 applied, 1 missing, 2 failed.</summary>
        private static int TryPatch(Harmony harmony, SpikeS9Hook hook)
        {
            string label = hook.Entry + " tool=" + hook.Tool + " layer=" + hook.Layer;
            try
            {
                MethodInfo target = hook.Args == null
                    ? AccessTools.DeclaredMethod(hook.Type, hook.Method)
                    : AccessTools.DeclaredMethod(hook.Type, hook.Method, hook.Args);
                if (target == null)
                {
                    ParsekLog.Warn(Tag, "hook missing: " + label);
                    return 1;
                }
                string prefix = hook.CheatReasonOnly ? nameof(CurrencyPrefix)
                    : hook.Type == typeof(ProgressNode) ? nameof(CheatCompletePrefix)
                    : nameof(ObservedPrefix);
                HookByMethod[target] = hook;
                harmony.Patch(target,
                    prefix: new HarmonyMethod(Own(prefix)),
                    finalizer: new HarmonyMethod(Own(nameof(ScopeFinalizer))));
                ParsekLog.Info(Tag, "hook applied: " + label);
                return 0;
            }
            catch (Exception ex)
            {
                ParsekLog.Warn(Tag, "hook failed: " + label + " " + ex.GetType().Name + ": " + ex.Message);
                return 2;
            }
        }

        private static MethodInfo Own(string name)
            => typeof(SpikeS9Cheats).GetMethod(name, BindingFlags.Static | BindingFlags.NonPublic);

        // ------------------------------------------------------------------ patches

        private static void ObservedPrefix(MethodBase __originalMethod, object[] __args)
        {
            try
            {
                if (!HookByMethod.TryGetValue(__originalMethod, out SpikeS9Hook hook)) return;
                Observe(__originalMethod, hook, SpikeS9Format.FormatArgs(__args));
            }
            catch (Exception ex) { WarnOnce("observed prefix", ex); }
        }

        private static void CheatCompletePrefix(MethodBase __originalMethod, ProgressNode __instance)
        {
            try
            {
                if (!HookByMethod.TryGetValue(__originalMethod, out SpikeS9Hook hook)) return;
                string node = __instance != null ? __instance.Id : "null";
                string done = __instance != null && __instance.IsComplete ? "true" : "false";
                Observe(__originalMethod, hook, "node=" + node + " alreadyComplete=" + done);
            }
            catch (Exception ex) { WarnOnce("CheatComplete prefix", ex); }
        }

        private static void CurrencyPrefix(MethodBase __originalMethod, object[] __args)
        {
            try
            {
                if (!HookByMethod.TryGetValue(__originalMethod, out SpikeS9Hook hook)) return;
                int i = SpikeS9Hooks.ReasonArgIndex(hook);
                if (i < 0 || __args == null || i >= __args.Length) return;
                if (!(__args[i] is TransactionReasons reason) || !SpikeS9Format.IsCheatingReason(reason)) return;
                Observe(__originalMethod, hook, SpikeS9Format.FormatArgs(__args));
            }
            catch (Exception ex) { WarnOnce("currency prefix", ex); }
        }

        private static void ScopeFinalizer(MethodBase __originalMethod)
        {
            Scope.PopIfTop(__originalMethod);
        }

        private static void Observe(MethodBase key, SpikeS9Hook hook, string detail)
        {
            int depth = Scope.Push(key, hook.Tool);
            Emit(SpikeS9Format.KindObserved, hook.Tool, hook.Layer, hook.Entry, Scope.Root, depth, detail);
        }

        // ------------------------------------------------------------------ events

        void Awake()
        {
            if (instance != null) { Destroy(gameObject); return; }
            instance = this;
            GameEvents.OnProgressReached.Add(OnProgressReached);
            GameEvents.OnProgressComplete.Add(OnProgressComplete);
            GameEvents.OnProgressAchieved.Add(OnProgressAchieved);
            GameEvents.OnFundsChanged.Add(OnFundsChanged);
            GameEvents.OnScienceChanged.Add(OnScienceChanged);
            GameEvents.OnReputationChanged.Add(OnReputationChanged);
            GameEvents.OnTechnologyResearched.Add(OnTechnologyResearched);
            GameEvents.OnKSCFacilityUpgraded.Add(OnKscFacilityUpgraded);
            GameEvents.onKerbalAdded.Add(OnKerbalAdded);
            GameEvents.onKerbalLevelUp.Add(OnKerbalLevelUp);
            GameEvents.Contract.onAccepted.Add(OnContractAccepted);
            GameEvents.Contract.onCompleted.Add(OnContractCompleted);
            ParsekLog.Info(Tag, "event host up: 12 GameEvents subscribed, poll period 1 s");
        }

        void OnDestroy()
        {
            if (instance != this) return;
            instance = null;
            GameEvents.OnProgressReached.Remove(OnProgressReached);
            GameEvents.OnProgressComplete.Remove(OnProgressComplete);
            GameEvents.OnProgressAchieved.Remove(OnProgressAchieved);
            GameEvents.OnFundsChanged.Remove(OnFundsChanged);
            GameEvents.OnScienceChanged.Remove(OnScienceChanged);
            GameEvents.OnReputationChanged.Remove(OnReputationChanged);
            GameEvents.OnTechnologyResearched.Remove(OnTechnologyResearched);
            GameEvents.OnKSCFacilityUpgraded.Remove(OnKscFacilityUpgraded);
            GameEvents.onKerbalAdded.Remove(OnKerbalAdded);
            GameEvents.onKerbalLevelUp.Remove(OnKerbalLevelUp);
            GameEvents.Contract.onAccepted.Remove(OnContractAccepted);
            GameEvents.Contract.onCompleted.Remove(OnContractCompleted);
        }

        private void OnProgressReached(ProgressNode n) => EventLine("OnProgressReached", "node=" + NodeId(n));
        private void OnProgressComplete(ProgressNode n) => EventLine("OnProgressComplete", "node=" + NodeId(n));
        private void OnProgressAchieved(ProgressNode n) => EventLine("OnProgressAchieved", "node=" + NodeId(n));

        private void OnFundsChanged(double v, TransactionReasons r)
            => EventLine("OnFundsChanged", "value=" + SpikeS9Format.FormatArg(v) + " reason=" + r);
        private void OnScienceChanged(float v, TransactionReasons r)
            => EventLine("OnScienceChanged", "value=" + SpikeS9Format.FormatArg(v) + " reason=" + r);
        private void OnReputationChanged(float v, TransactionReasons r)
            => EventLine("OnReputationChanged", "value=" + SpikeS9Format.FormatArg(v) + " reason=" + r);

        private void OnTechnologyResearched(GameEvents.HostTargetAction<RDTech, RDTech.OperationResult> d)
            => EventLine("OnTechnologyResearched", "tech=" + (d.host != null ? d.host.techID : "null") + " result=" + d.target);

        private void OnKscFacilityUpgraded(Upgradeables.UpgradeableFacility f, int level)
            => EventLine("OnKSCFacilityUpgraded", "facility=" + (f != null ? f.id : "null")
                + " level=" + level.ToString(CultureInfo.InvariantCulture));

        private void OnKerbalAdded(ProtoCrewMember p) => EventLine("onKerbalAdded", "kerbal=" + (p != null ? p.name : "null"));
        private void OnKerbalLevelUp(ProtoCrewMember p) => EventLine("onKerbalLevelUp", "kerbal=" + (p != null ? p.name : "null"));

        private void OnContractAccepted(Contracts.Contract c) => EventLine("Contract.onAccepted", ContractText(c));
        private void OnContractCompleted(Contracts.Contract c) => EventLine("Contract.onCompleted", ContractText(c));

        private static string NodeId(ProgressNode n) => n != null ? n.Id : "null";

        private static string ContractText(Contracts.Contract c)
            => c == null ? "contract=null"
                : "contract=" + c.GetType().Name + " id=" + c.ContractID.ToString(CultureInfo.InvariantCulture);

        private static void EventLine(string name, string detail)
        {
            try
            {
                Emit(SpikeS9Format.KindEvent, "-", "event", name, Scope.Root, Scope.Depth, detail);
            }
            catch (Exception ex) { WarnOnce("event " + name, ex); }
        }

        // ------------------------------------------------------------------ poll

        void Update()
        {
            float now = Time.realtimeSinceStartup;
            if (now < nextPoll) return;
            nextPoll = now + PollPeriodSeconds;
            try { Poll(); }
            catch (Exception ex) { WarnOnce("poll", ex); }
        }

        private void Poll()
        {
            Dictionary<string, string> snap = ReadSnapshot();
            object game = HighLogic.CurrentGame;
            string scene = HighLogic.LoadedScene.ToString();

            if (!string.Equals(scene, lastInventoryScene, StringComparison.Ordinal))
            {
                lastInventoryScene = scene;
                Emit(SpikeS9Format.KindInventory, "-", "poll", "inventory", SpikeS9Format.NoScope, 0, Inventory());
            }

            if (lastSnapshot == null)
            {
                Emit(SpikeS9Format.KindBaseline, "-", "poll", "baseline", SpikeS9Format.NoScope, 0,
                    SpikeS9Format.FormatSnapshot(snap));
            }
            else if (SpikeS9Format.ShouldRebaseline(lastGame, game, lastScene, scene))
            {
                var changes = SpikeS9Format.Diff(lastSnapshot, snap);
                string reason = !ReferenceEquals(lastGame, game) ? "game" : "scene";
                Emit(SpikeS9Format.KindRebaseline, "-", "poll", "rebaseline", SpikeS9Format.NoScope, 0,
                    "reason=" + reason + " changes=" + SpikeS9Format.FormatChanges(changes));
            }
            else
            {
                foreach (var c in SpikeS9Format.Diff(lastSnapshot, snap))
                    Emit(SpikeS9Format.KindPoll, c.Key, "poll", c.Key, Scope.Root, Scope.Depth,
                        "old=" + c.Old + " new=" + c.New);
            }
            lastSnapshot = snap;
            lastGame = game;
            lastScene = scene;
        }

        private static FieldInfo[] cheatOptionFields;

        [MethodImpl(MethodImplOptions.NoInlining)]
        private static Dictionary<string, string> ReadSnapshot()
        {
            var snap = new Dictionary<string, string>(StringComparer.Ordinal);
            if (cheatOptionFields == null)
                cheatOptionFields = typeof(CheatOptions).GetFields(BindingFlags.Public | BindingFlags.Static);
            foreach (FieldInfo f in cheatOptionFields)
            {
                if (f.FieldType != typeof(bool)) continue;
                snap["CheatOptions." + f.Name] = SpikeS9Format.FormatArg(f.GetValue(null));
            }

            // HackGravity: the factor of the first widget that is not at 1 (the prefab reads 1),
            // the same read as GravityHackDetector, plus its protected "hacked" flag.
            string factor = "1";
            bool hacked = false;
            FieldInfo hackedField = AccessTools.Field(typeof(HackGravity), "hacked");
            foreach (HackGravity w in UnityEngine.Resources.FindObjectsOfTypeAll<HackGravity>())
            {
                if (w == null) continue;
                if (Math.Abs(w.gravityFactor - 1.0) > 1e-9 && factor == "1")
                    factor = SpikeS9Format.FormatArg(w.gravityFactor);
                if (hackedField != null && hackedField.GetValue(w) is bool b && b) hacked = true;
            }
            snap["HackGravity.gravityFactor"] = factor;
            snap["HackGravity.hacked"] = SpikeS9Format.FormatArg(hacked);

            bool armed = false;
            FieldInfo armedField = AccessTools.Field(typeof(CheatsObjectThrower), "isArmed");
            foreach (CheatsObjectThrower t in UnityEngine.Resources.FindObjectsOfTypeAll<CheatsObjectThrower>())
                if (t != null && armedField != null && armedField.GetValue(t) is bool b && b) armed = true;
            snap["CheatsObjectThrower.isArmed"] = SpikeS9Format.FormatArg(armed);

            snap["DebugToolbar.toolbarShown"] = SpikeS9Format.FormatArg(DebugToolbar.toolbarShown);

            // The Alt+F12 Difficulty screen writes these six game parameters (and the
            // IgnoreAgencyMindsetOnContracts cheat option) through compiler-generated lambdas.
            Game game = HighLogic.CurrentGame;
            if (game?.Parameters != null)
            {
                snap["Difficulty.AllowStockVessels"] = SpikeS9Format.FormatArg(game.Parameters.Difficulty.AllowStockVessels);
                snap["Difficulty.MissingCrewsRespawn"] = SpikeS9Format.FormatArg(game.Parameters.Difficulty.MissingCrewsRespawn);
                snap["Flight.CanRestart"] = SpikeS9Format.FormatArg(game.Parameters.Flight.CanRestart);
                snap["Flight.CanLeaveToEditor"] = SpikeS9Format.FormatArg(game.Parameters.Flight.CanLeaveToEditor);
                snap["Flight.CanQuickSave"] = SpikeS9Format.FormatArg(game.Parameters.Flight.CanQuickSave);
                snap["Flight.CanQuickLoad"] = SpikeS9Format.FormatArg(game.Parameters.Flight.CanQuickLoad);
            }
            return snap;
        }

        [MethodImpl(MethodImplOptions.NoInlining)]
        private static string Inventory()
        {
            return "scene=" + HighLogic.LoadedScene
                + " DebugToolbar=" + Count<DebugToolbar>()
                + " ConsoleCommands=" + Count<ConsoleCommands>()
                + " CheatsCareer=" + Count<CheatsCareer>()
                + " HackGravity=" + Count<HackGravity>()
                + " CheatsObjectThrower=" + Count<CheatsObjectThrower>()
                + " CheatsComets=" + Count<CheatsComets>();
        }

        private static string Count<T>() where T : UnityEngine.Object
        {
            T[] all = UnityEngine.Resources.FindObjectsOfTypeAll<T>();
            return (all != null ? all.Length : 0).ToString(CultureInfo.InvariantCulture);
        }

        // ------------------------------------------------------------------ output

        private static void Emit(string kind, string tool, string layer, string entry, string scope, int depth, string detail)
        {
            long n = ++seq;
            int frame = ReadFrame();
            float realtime = ReadRealtime();
            string ut = ReadUt();
            string scene = HighLogic.LoadedScene.ToString();

            var fields = new List<KeyValuePair<string, string>>
            {
                new KeyValuePair<string, string>("tool", tool),
                new KeyValuePair<string, string>("layer", layer),
                new KeyValuePair<string, string>("entry", entry),
                new KeyValuePair<string, string>("scope", scope),
                new KeyValuePair<string, string>("depth", depth.ToString(CultureInfo.InvariantCulture)),
                new KeyValuePair<string, string>("frame", frame.ToString(CultureInfo.InvariantCulture)),
                new KeyValuePair<string, string>("ut", ut),
                new KeyValuePair<string, string>("scene", scene),
            };
            ParsekLog.Info(Tag, SpikeS9Format.FormatLine(kind, n, fields, detail));
            try
            {
                SpikeCsv.AppendRow(SpikeS9Format.SpikeId, SpikeS9Format.CsvName, SpikeS9Format.CsvHeader,
                    Session, n, frame, realtime, ut, scene, kind, tool, layer, entry, scope, depth, detail);
            }
            catch (Exception ex) { WarnOnce("csv", ex); }
        }

        [MethodImpl(MethodImplOptions.NoInlining)]
        private static int ReadFrame() => Time.frameCount;

        [MethodImpl(MethodImplOptions.NoInlining)]
        private static float ReadRealtime() => Time.realtimeSinceStartup;

        [MethodImpl(MethodImplOptions.NoInlining)]
        private static string ReadUt()
        {
            try
            {
                if (Planetarium.fetch == null) return "none";
                return Planetarium.GetUniversalTime().ToString("R", CultureInfo.InvariantCulture);
            }
            catch (Exception) { return "none"; }
        }

        private static readonly HashSet<string> warned = new HashSet<string>(StringComparer.Ordinal);

        private static void WarnOnce(string where, Exception ex)
        {
            if (!warned.Add(where)) return;
            ParsekLog.Warn(Tag, where + " failed: " + ex.GetType().Name + ": " + ex.Message);
        }

        [MethodImpl(MethodImplOptions.NoInlining)]
        private static string SafeCsvPath()
        {
            try { return SpikeCsv.PathFor(SpikeS9Format.SpikeId, SpikeS9Format.CsvName); }
            catch (Exception ex) { return "<unresolved: " + ex.Message + ">"; }
        }
    }
}
