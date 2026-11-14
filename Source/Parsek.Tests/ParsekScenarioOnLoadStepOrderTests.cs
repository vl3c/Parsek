using System;
using System.Collections;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Reflection.Emit;
using System.Text;
using HarmonyLib;
using Xunit;

namespace Parsek.Tests
{
    /// <summary>
    /// Headless run of the real <c>ParsekScenario.OnLoad</c> for every load kind, pinning the
    /// ORDER of the steps it actually executes as one table per kind (golden
    /// <c>Fixtures/SourceCharacterization/ParsekScenario.OnLoad.steps.txt</c>).
    ///
    /// <para><b>What a step is.</b> While a load runs, every method of
    /// <see cref="ParsekScenario"/> whose name starts with <c>OnLoad</c> (OnLoad itself and the
    /// methods it delegates to) is transpiled to record, in execution order:
    /// <c>phase NAME</c> when one of the OnLoad phase names is loaded (the
    /// <c>loadPhase</c> assignment), and <c>call Type.Method</c> before each call it makes into
    /// Parsek code (property accessors, <c>ParsekLog</c>, compiler-generated members and other
    /// <c>OnLoad*</c> members excluded: those are part of the flow, not steps). Calls made
    /// deeper down are not steps. The table also carries the classification line, every
    /// coroutine started, every seam hit, and the final phase and status of the load's timing
    /// line.</para>
    ///
    /// <para><b>Named seams the test replaces</b> (they cannot run headless; each hit is a
    /// <c>seam</c> row): <c>UnityEngine.Time.realtimeSinceStartup</c> /
    /// <c>UnityEngine.Time.frameCount</c> read inline in OnLoad (fixed values);
    /// <c>MonoBehaviour.StartCoroutine</c> (recorded, not started); and the methods in
    /// <see cref="SkippedSeams"/>, which reach Unity natives (<c>Object.FindObjectsOfType</c>)
    /// and are skipped. Everything else runs for real over an empty save node.</para>
    ///
    /// <para><b>The scenarios.</b> Cold is a boot load into the Space Center. Every other kind
    /// first runs a cold load and destroys that scenario (stock's teardown, which captures the
    /// in-session handoff), then arms the kind's real input and loads again; only the second
    /// load is recorded. Data-dependent phases (merge journal, sweep recalculation, a pending
    /// tree, the backup hold) do not appear over an empty save; the token characterization
    /// (<see cref="ParsekScenarioOnLoadCharacterizationTests"/>) pins their order in source.</para>
    /// </summary>
    [Collection("Sequential")]
    public class ParsekScenarioOnLoadStepOrderTests : IDisposable
    {
        private const string HarmonyId = "parsek.tests.onload-step-order";
        private const string GoldenFile = "ParsekScenario.OnLoad.steps.txt";

        /// <summary>Methods skipped headless (Unity natives below them).</summary>
        internal static readonly string[] SkippedSeams =
        {
            "Parsek.GameStateFacilityRecorder::SeedFacilityCacheFromCurrentState",
            "Parsek.GameStateFacilityRecorder::PollFacilityState",
        };

        /// <summary>Every phase name OnLoad assigns (its <c>loadPhase</c> literals).</summary>
        internal static readonly HashSet<string> PhaseNames = new HashSet<string>(StringComparer.Ordinal)
        {
            "entry", "reset-dialog", "settings", "save-format", "hooks", "load-classify",
            "save-scoped-reset", "crew-groups", "refly-state-load", "stock-pool-reference-load",
            "ledger-held-window-load", "rewind-vessel-holds-load", "playback-replay-scope-load",
            "removed-replay-flags-load", "rewind-point-carry-over", "rewind-staged-lists-carry-over",
            "in-session-staged-handoff", "pre-parsek-backup", "game-state-recorder",
            "initial-baseline", "timeline-break", "rewind", "vessel-names-apply",
            "active-tree-restore", "in-session-handoff-step-b", "revert-classification",
            "vessel-names-revert-undo", "revert-cleanup", "tree-mutable-state", "revert-strip",
            "tree-state-restore", "discard-refly-vessel-holds", "revert-milestones",
            "scene-milestones", "limbo-dispatch", "pending-outside-flight", "route-load-reconcile",
            "ledger-recalculate", "sidecar-reconcile", "rewind-post-load", "merge-journal",
            "load-time-sweep", "load-time-sweep-recalculate", "test-batch-reconcile",
            "rewind-point-reap", "file-collection", "cold-start", "quarantine-restore",
            "durable-committed-id-hint", "stale-pending-discard", "clear-committed",
            "chain-validation", "vessel-names", "recording-trees", "sanitize-nonloopable-loop",
            "missions", "cold-active-tree-restore", "cold-attempt-events", "milestones",
            "ledger-load", "routes", "route-loop-reconcile", "deferred-seed", "load-summary",
            "optimization", "crew-auto-unreserve", "quit-without-saving-reap",
        };

        private readonly Harmony harmony = new Harmony(HarmonyId);
        private readonly List<string> logLines = new List<string>();
        private readonly bool priorInitialLoadDone;
        private readonly GameScenes priorLastOnSaveScene;
        private readonly GameScenes priorScene;
        private readonly Planetarium priorPlanetarium;
        private readonly Game priorGame;
        private readonly FlightDriver.StartupBehaviours priorStartup;

        public ParsekScenarioOnLoadStepOrderTests()
        {
            priorInitialLoadDone = (bool)StaticField("initialLoadDone").GetValue(null);
            priorLastOnSaveScene = (GameScenes)StaticField("lastOnSaveScene").GetValue(null);
            priorScene = HighLogic.LoadedScene;
            priorPlanetarium = Planetarium.fetch;
            priorGame = HighLogic.CurrentGame;
            priorStartup = FlightDriver.StartupBehaviour;
            ResetStatics();
            ParsekLog.ResetTestOverrides();
            ParsekLog.TestSinkForTesting = line => logLines.Add(line);
            ParsekLog.VerboseOverrideForTesting = true;
            StepRecorder.Steps = null;
            InstallSeams();
        }

        public void Dispose()
        {
            harmony.UnpatchAll(HarmonyId);
            StepRecorder.Steps = null;
            ResetStatics();
            StaticField("initialLoadDone").SetValue(null, priorInitialLoadDone);
            StaticField("lastOnSaveScene").SetValue(null, priorLastOnSaveScene);
            HighLogic.LoadedScene = priorScene;
            Planetarium.fetch = priorPlanetarium;
            HighLogic.CurrentGame = priorGame;
            FlightDriver.StartupBehaviour = priorStartup;
            ParsekLog.ResetTestOverrides();
            ParsekLog.SuppressLogging = true;
        }

        // ---- the pinned table ----

        [Fact]
        public void EveryLoadKind_RunsItsPinnedStepOrder()
        {
            var actual = new StringBuilder();
            foreach (Scenario scenario in Scenarios())
            {
                actual.Append("## ").Append(scenario.Kind).Append('\n');
                foreach (string row in RunScenario(scenario))
                    actual.Append(row).Append('\n');
                actual.Append('\n');
            }

            string goldenPath = ParsekScenarioOnLoadCharacterizationTests.GoldenPath(GoldenFile);
            List<string> expected = File.Exists(goldenPath)
                ? File.ReadAllLines(goldenPath).ToList()
                : new List<string> { "<golden step table missing: " + goldenPath + ">" };
            List<string> got = actual.ToString().Split('\n').ToList();
            while (expected.Count > 0 && expected[expected.Count - 1].Length == 0) expected.RemoveAt(expected.Count - 1);
            while (got.Count > 0 && got[got.Count - 1].Length == 0) got.RemoveAt(got.Count - 1);
            if (!expected.SequenceEqual(got))
            {
                string actualPath = Path.Combine(
                    AppDomain.CurrentDomain.BaseDirectory, "ParsekScenario.OnLoad.steps.actual.txt");
                File.WriteAllText(actualPath, actual.ToString());
                int i = 0;
                while (i < expected.Count && i < got.Count && expected[i] == got[i]) i++;
                Assert.True(false,
                    "OnLoad's executed step table diverges from the pinned one at line " + (i + 1)
                    + ": expected " + (i < expected.Count ? "'" + expected[i] + "'" : "<end>")
                    + ", actual " + (i < got.Count ? "'" + got[i] + "'" : "<end>")
                    + " (actual table written to " + actualPath + ")");
            }
        }

        [Fact]
        public void EveryLoadKind_ClassifiesAsItsKindAndCompletes()
        {
            foreach (Scenario scenario in Scenarios())
            {
                List<string> rows = RunScenario(scenario);
                string classified = rows.Single(r => r.StartsWith("classified ", StringComparison.Ordinal));
                Assert.Contains("refined=" + scenario.Kind + " ", classified + " ");
                Assert.DoesNotContain(rows, r => r.StartsWith("threw ", StringComparison.Ordinal));
                Assert.Equal(1, rows.Count(r => r.StartsWith("timing ", StringComparison.Ordinal)));
            }
        }

        [Fact]
        public void TheRecorder_SeesAPhaseOnlyWhereOnLoadAssignsIt()
        {
            // The last phase row of every kind is the phase the load's own timing line names.
            foreach (Scenario scenario in Scenarios())
            {
                List<string> rows = RunScenario(scenario);
                string lastPhase = rows.Last(r => r.StartsWith("phase ", StringComparison.Ordinal)).Substring(6);
                string timing = rows.Single(r => r.StartsWith("timing ", StringComparison.Ordinal));
                Assert.StartsWith("timing phase=" + lastPhase + " ", timing);
            }
        }

        // ---- the scenarios ----

        private sealed class Scenario
        {
            internal string Kind;
            internal GameScenes Scene;
            internal GameScenes LastOnSaveScene = GameScenes.SPACECENTER;
            internal double PreChangeUT = -1.0;
            internal bool PrimeWithColdLoad = true;
            internal Action Arm;
        }

        private static IEnumerable<Scenario> Scenarios()
        {
            yield return new Scenario
            {
                Kind = "Cold", Scene = GameScenes.SPACECENTER, PrimeWithColdLoad = false,
            };
            yield return new Scenario
            {
                Kind = "PlainRewind", Scene = GameScenes.SPACECENTER,
                Arm = () => RewindContext.BeginRewind(50.0, default(BudgetSummary), 0, 0, 0),
            };
            yield return new Scenario
            {
                Kind = "ReFlyStart", Scene = GameScenes.FLIGHT,
                Arm = () => RewindInvokeContext.Pending = true,
            };
            yield return new Scenario
            {
                Kind = "DiscardReFly", Scene = GameScenes.SPACECENTER, LastOnSaveScene = GameScenes.FLIGHT,
                Arm = () => DiscardReFlyLoadIntent.Arm(RevertTarget.Launch, "sess-step-order"),
            };
            yield return new Scenario
            {
                Kind = "StockRevert", Scene = GameScenes.FLIGHT, LastOnSaveScene = GameScenes.FLIGHT,
                PreChangeUT = 500.0,
                Arm = () => RevertDetector.SetPendingForTesting(RevertKind.Launch),
            };
            yield return new Scenario
            {
                Kind = "QuickloadFlight", Scene = GameScenes.FLIGHT, LastOnSaveScene = GameScenes.FLIGHT,
                PreChangeUT = 500.0,
            };
            yield return new Scenario
            {
                Kind = "InSessionOther", Scene = GameScenes.SPACECENTER, LastOnSaveScene = GameScenes.FLIGHT,
            };
        }

        private List<string> RunScenario(Scenario scenario)
        {
            ResetStatics();
            StaticField("initialLoadDone").SetValue(null, false);
            InstallPlanetarium(100.0);
            // No stock game: every step that reads one takes its no-game path.
            HighLogic.CurrentGame = null;
            FlightDriver.StartupBehaviour = FlightDriver.StartupBehaviours.RESUME_SAVED_FILE;

            if (scenario.PrimeWithColdLoad)
            {
                HighLogic.LoadedScene = GameScenes.SPACECENTER;
                var prime = new ParsekScenario();
                ParsekScenario.SetInstanceForTesting(prime);
                StepRecorder.Steps = null;
                prime.OnLoad(new ConfigNode("SCENARIO"));
                prime.OnDestroy();
            }

            HighLogic.LoadedScene = scenario.Scene;
            StaticField("lastOnSaveScene").SetValue(null, scenario.LastOnSaveScene);
            if (scenario.PreChangeUT >= 0)
                ParsekScenario.StampSceneChangeRequestedUT(scenario.PreChangeUT);
            scenario.Arm?.Invoke();

            var instance = new ParsekScenario();
            ParsekScenario.SetInstanceForTesting(instance);
            logLines.Clear();
            var steps = new List<string>();
            StepRecorder.Steps = steps;
            try
            {
                instance.OnLoad(new ConfigNode("SCENARIO"));
            }
            catch (Exception ex)
            {
                steps.Add("threw " + ex.GetType().Name);
            }
            finally
            {
                StepRecorder.Steps = null;
                instance.OnDestroy();
            }

            var rows = new List<string>();
            string classified = logLines.FirstOrDefault(
                l => l.Contains("[" + LoadReconcilePolicy.LogTag + "]") && l.Contains("Load classified:"));
            rows.Add("classified " + ClassificationKinds(classified));
            rows.AddRange(steps);
            string timing = logLines.LastOrDefault(l => l.Contains("[Diagnostics] OnLoad: "));
            rows.Add("timing " + TimingTail(timing));
            ResetStatics();
            return rows;
        }

        /// <summary>"early=X refined=Y" out of the classification line.</summary>
        private static string ClassificationKinds(string line)
        {
            if (line == null) return "<none>";
            string early = Token(line, "early=");
            string refined = Token(line, "refined=");
            return "early=" + early + " refined=" + refined;
        }

        /// <summary>"phase=X status=Y" out of the load's timing line.</summary>
        private static string TimingTail(string line)
        {
            if (line == null) return "<none>";
            return "phase=" + Token(line, "phase=") + " status=" + Token(line, "status=");
        }

        private static string Token(string line, string key)
        {
            int at = line.IndexOf(key, StringComparison.Ordinal);
            if (at < 0) return "<missing>";
            int start = at + key.Length;
            int end = start;
            while (end < line.Length && !char.IsWhiteSpace(line[end])) end++;
            return line.Substring(start, end - start);
        }

        // ---- headless state ----

        private static FieldInfo StaticField(string name)
        {
            FieldInfo field = typeof(ParsekScenario).GetField(
                name, BindingFlags.Static | BindingFlags.NonPublic | BindingFlags.Public);
            Assert.True(field != null, "ParsekScenario." + name + " not found");
            return field;
        }

        private static void InstallPlanetarium(double ut)
        {
            Planetarium p = BackgroundRecorderCharacterizationSupport.Alive<Planetarium>();
            p.time = ut;
            Planetarium.fetch = p;
        }

        private static void ResetStatics()
        {
            RecordingStore.ResetForTesting();
            ParsekScenario.ResetInstanceForTesting();
            RewindContext.ResetForTesting();
            RewindInvokeContext.ResetForTesting();
            DiscardReFlyLoadIntent.ResetForTesting();
            RevertDetector.ResetForTesting();
            SaveFileLoadIntent.ResetForTesting();
            InSessionStagedStateHandoff.ResetForTesting();
        }

        // ---- the seams ----

        private void InstallSeams()
        {
            var transpiler = new HarmonyMethod(typeof(StepRecorder), nameof(StepRecorder.Transpile));
            foreach (MethodInfo method in typeof(ParsekScenario).GetMethods(
                         BindingFlags.Instance | BindingFlags.Static | BindingFlags.Public
                         | BindingFlags.NonPublic | BindingFlags.DeclaredOnly))
            {
                if (!method.Name.StartsWith("OnLoad", StringComparison.Ordinal)) continue;
                if (method.IsGenericMethodDefinition || method.IsAbstract) continue;
                harmony.Patch(method, transpiler: transpiler);
            }

            MethodInfo startCoroutine = typeof(UnityEngine.MonoBehaviour).GetMethod(
                "StartCoroutine", new[] { typeof(IEnumerator) });
            harmony.Patch(startCoroutine,
                prefix: new HarmonyMethod(typeof(StepRecorder), nameof(StepRecorder.StartCoroutinePrefix)));

            foreach (string seam in SkippedSeams)
            {
                int sep = seam.IndexOf("::", StringComparison.Ordinal);
                Type type = typeof(ParsekScenario).Assembly.GetType(seam.Substring(0, sep), throwOnError: true);
                MethodInfo[] targets = type.GetMethods(
                        BindingFlags.Instance | BindingFlags.Static | BindingFlags.Public
                        | BindingFlags.NonPublic | BindingFlags.DeclaredOnly)
                    .Where(m => m.Name == seam.Substring(sep + 2))
                    .ToArray();
                Assert.True(targets.Length == 1, "seam " + seam + " resolves to " + targets.Length + " methods");
                harmony.Patch(targets[0],
                    prefix: new HarmonyMethod(typeof(StepRecorder), nameof(StepRecorder.SkipSeamPrefix)));
            }
        }

        /// <summary>The recorder the transpiled OnLoad methods call. Static: Harmony calls it.</summary>
        public static class StepRecorder
        {
            internal static List<string> Steps;

            public static float FixedRealtimeSinceStartup() => 1000f;
            public static int FixedFrameCount() => 10000;

            public static string RecordLiteral(string value)
            {
                if (Steps != null && value != null && PhaseNames.Contains(value))
                    Steps.Add("phase " + value);
                return value;
            }

            public static void RecordCall(string name)
            {
                Steps?.Add("call " + name);
            }

            public static bool StartCoroutinePrefix(IEnumerator routine, ref UnityEngine.Coroutine __result)
            {
                string name = routine == null ? "<null>" : routine.GetType().Name;
                int open = name.IndexOf('<');
                int close = name.IndexOf('>');
                if (open >= 0 && close > open) name = name.Substring(open + 1, close - open - 1);
                Steps?.Add("coroutine " + name);
                __result = null;
                return false;
            }

            public static bool SkipSeamPrefix(MethodBase __originalMethod)
            {
                Steps?.Add("seam " + __originalMethod.DeclaringType.Name + "." + __originalMethod.Name);
                return false;
            }

            internal static bool IsStep(MethodBase callee)
            {
                Type type = callee.DeclaringType;
                if (type == null || type.Assembly != typeof(ParsekScenario).Assembly) return false;
                if (callee.IsSpecialName || callee.IsConstructor) return false;
                if (callee.Name.StartsWith("<", StringComparison.Ordinal)) return false;
                if (type == typeof(ParsekLog)) return false;
                for (Type t = type; t != null; t = t.DeclaringType)
                {
                    if (t.Name.StartsWith("<", StringComparison.Ordinal)) return false;
                    if (t.Name.StartsWith("OnLoad", StringComparison.Ordinal)) return false;
                }
                if (type == typeof(ParsekScenario) && callee.Name.StartsWith("OnLoad", StringComparison.Ordinal))
                    return false;
                return true;
            }

            public static IEnumerable<CodeInstruction> Transpile(IEnumerable<CodeInstruction> instructions)
            {
                MethodInfo realtime = AccessTools.PropertyGetter(typeof(UnityEngine.Time), "realtimeSinceStartup");
                MethodInfo frameCount = AccessTools.PropertyGetter(typeof(UnityEngine.Time), "frameCount");
                MethodInfo recordLiteral = AccessTools.Method(typeof(StepRecorder), nameof(RecordLiteral));
                MethodInfo recordCall = AccessTools.Method(typeof(StepRecorder), nameof(RecordCall));
                foreach (CodeInstruction instruction in instructions)
                {
                    bool isCall = instruction.opcode == OpCodes.Call || instruction.opcode == OpCodes.Callvirt;
                    if (isCall && Equals(instruction.operand, realtime))
                    {
                        instruction.operand = AccessTools.Method(typeof(StepRecorder), nameof(FixedRealtimeSinceStartup));
                        yield return instruction;
                        continue;
                    }
                    if (isCall && Equals(instruction.operand, frameCount))
                    {
                        instruction.operand = AccessTools.Method(typeof(StepRecorder), nameof(FixedFrameCount));
                        yield return instruction;
                        continue;
                    }
                    if (isCall && instruction.operand is MethodBase callee && IsStep(callee))
                    {
                        var record = new CodeInstruction(OpCodes.Ldstr, callee.DeclaringType.Name + "." + callee.Name);
                        record.labels.AddRange(instruction.labels);
                        instruction.labels.Clear();
                        // A block that opens at the call opens at the record instead; one that
                        // closes after it stays with it.
                        foreach (ExceptionBlock block in instruction.blocks.ToList())
                        {
                            if (block.blockType == ExceptionBlockType.EndExceptionBlock) continue;
                            record.blocks.Add(block);
                            instruction.blocks.Remove(block);
                        }
                        yield return record;
                        yield return new CodeInstruction(OpCodes.Call, recordCall);
                        yield return instruction;
                        continue;
                    }
                    yield return instruction;
                    if (instruction.opcode == OpCodes.Ldstr)
                        yield return new CodeInstruction(OpCodes.Call, recordLiteral);
                }
            }
        }
    }
}
