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
    /// <c>ctx.Phase</c> assignment), and <c>call Type.Method</c> before each call it makes into
    /// Parsek code (property accessors, <c>ParsekLog</c>, compiler-generated members and other
    /// <c>OnLoad*</c> members excluded: those are part of the flow, not steps). Calls made
    /// deeper down are not steps. The table also carries the classification line, every
    /// coroutine started, every seam hit, and the final phase and status of the load's timing
    /// line.</para>
    ///
    /// <para><b>Named seams the test replaces</b> (they cannot run headless): OnLoad's two
    /// Unity clock reads, <c>ParsekScenario.ReadUnityRealtimeSinceStartupCore</c> /
    /// <c>ReadUnityFrameCountCore</c>, swapped at the call site for fixed values;
    /// <c>MonoBehaviour.StartCoroutine</c>, swapped at the call site in the <c>OnLoad*</c>
    /// methods and in <see cref="CoroutineSeamHosts"/> for a recorder that writes a
    /// <c>coroutine</c> row and starts nothing; and the methods in <see cref="SkippedSeams"/>,
    /// which reach Unity natives (<c>Object.FindObjectsOfType</c>) and are skipped with a
    /// <c>seam</c> row. Everything else runs for real over an empty save node.</para>
    ///
    /// <para><b>Why call-site swaps.</b> Harmony compiles the original of every method it
    /// patches, and the Windows .NET test host cannot compile a method that calls a Unity
    /// internalcall directly (<c>SecurityException</c>: ECall methods must be packaged into a
    /// system module; mono only fails when the call runs). So no patched method may call one
    /// directly, and no Unity method is patched at all (<c>StartCoroutine</c> itself calls
    /// two). <see cref="NoPatchedMethod_CallsAUnityInternalcallDirectly"/> holds that on every
    /// host.</para>
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

        /// <summary>Methods outside <c>OnLoad*</c> the loads run that start a coroutine
        /// (transpiled for the coroutine swap only: no step rows).</summary>
        internal static readonly string[] CoroutineSeamHosts =
        {
            "HandleRewindOnLoad",
        };

        /// <summary>Every phase name OnLoad assigns (its <c>ctx.Phase</c> literals).</summary>
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
        private string lastPhaseTrace;
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

        [Fact]
        public void TheProductionPhaseTrace_NamesEveryRecordedPhaseInOrder()
        {
            // OnLoad's own once-per-load trace line (from its finally) names the load kind,
            // the final status and the same phase sequence the transpiled recorder saw.
            foreach (Scenario scenario in Scenarios())
            {
                List<string> rows = RunScenario(scenario);
                Assert.True(lastPhaseTrace != null, scenario.Kind + ": no 'Load phases:' line");
                Assert.Contains("[VERBOSE][" + LoadReconcilePolicy.LogTag + "]", lastPhaseTrace);
                Assert.Equal(scenario.Kind, Token(lastPhaseTrace, "kind="));
                string timing = rows.Single(r => r.StartsWith("timing ", StringComparison.Ordinal));
                Assert.EndsWith(" status=" + Token(lastPhaseTrace, "status="), timing);

                int open = lastPhaseTrace.IndexOf('[', lastPhaseTrace.IndexOf("Load phases:", StringComparison.Ordinal));
                int close = lastPhaseTrace.LastIndexOf(']');
                List<string> traced = lastPhaseTrace.Substring(open + 1, close - open - 1)
                    .Split(new[] { " > " }, StringSplitOptions.None)
                    .Select(entry => entry.Substring(0, entry.LastIndexOf('@')))
                    .ToList();
                List<string> recorded = rows
                    .Where(r => r.StartsWith("phase ", StringComparison.Ordinal))
                    .Select(r => r.Substring(6))
                    .ToList();
                Assert.Equal(recorded, traced);
                Assert.Equal(traced.Count.ToString(System.Globalization.CultureInfo.InvariantCulture),
                    Token(lastPhaseTrace, "count="));
            }
        }

        [Fact]
        public void EveryLoadKind_PassesItsBranchArgumentsToTheSharedTail()
        {
            // The shared tail takes the branch's collector mode and vessel-name reason as
            // arguments, which no step row shows: pin them per kind.
            harmony.Patch(
                AccessTools.Method(typeof(ParsekFileCollector), nameof(ParsekFileCollector.RunAtLoad)),
                prefix: new HarmonyMethod(typeof(StepRecorder), nameof(StepRecorder.RunAtLoadArgsPrefix)));
            harmony.Patch(
                AccessTools.Method(typeof(VesselNameStore), nameof(VesselNameStore.ApplyAll)),
                prefix: new HarmonyMethod(typeof(StepRecorder), nameof(StepRecorder.ApplyAllArgsPrefix)));
            harmony.Patch(
                AccessTools.Method(typeof(VesselNameStore), nameof(VesselNameStore.RunSpawnLinkBackfill)),
                prefix: new HarmonyMethod(typeof(StepRecorder), nameof(StepRecorder.BackfillArgsPrefix)));

            var expected = new Dictionary<string, string[]>(StringComparer.Ordinal)
            {
                ["Cold"] = new[] { "collector Cold coldBranch=True", "backfill onload-tail:cold", "names onload-tail:cold" },
                ["PlainRewind"] = new[] { "backfill onload-tail:rewind", "names onload-tail:rewind" },
                ["ReFlyStart"] = new[] { "collector ReFlyStart coldBranch=False", "backfill onload-tail:scene-change", "names onload-tail:scene-change" },
                ["DiscardReFly"] = new[] { "collector DiscardReFly coldBranch=False", "backfill onload-tail:scene-change", "names onload-tail:scene-change" },
                ["StockRevert"] = new[] { "collector InSession coldBranch=False", "backfill onload-tail:revert", "names onload-tail:revert" },
                ["QuickloadFlight"] = new[] { "collector InSession coldBranch=False", "backfill onload-tail:scene-change", "names onload-tail:scene-change" },
                ["InSessionOther"] = new[] { "collector InSession coldBranch=False", "backfill onload-tail:scene-change", "names onload-tail:scene-change" },
            };
            foreach (Scenario scenario in Scenarios())
            {
                List<string> rows = RunScenario(scenario);
                List<string> args = rows
                    .Where(r => r.StartsWith("arg ", StringComparison.Ordinal))
                    .Select(r => r.Substring(4))
                    .Where(r => !r.StartsWith("names ", StringComparison.Ordinal)
                        && !r.StartsWith("backfill ", StringComparison.Ordinal)
                        || r.Contains(" onload-tail:"))
                    .ToList();
                Assert.True(expected[scenario.Kind].SequenceEqual(args),
                    scenario.Kind + ": expected [" + string.Join(", ", expected[scenario.Kind])
                    + "], actual [" + string.Join(", ", args) + "]");
            }
        }

        [Fact]
        public void AColdLoadWhoseBackupFails_StopsInThePrologueAndRunsNoBranch()
        {
            // The backup hold is data-dependent (a save that needs a backup the copy cannot
            // make), so the table never shows it: force the outcome and pin where the load stops.
            harmony.Patch(
                AccessTools.Method(typeof(PreParsekBackup), nameof(PreParsekBackup.MaybeBackupOnFirstColdContact)),
                prefix: new HarmonyMethod(typeof(StepRecorder), nameof(StepRecorder.BackupFailsPrefix)));
            // The notice call is swapped where the hold posts it: QueueNoticeForSettledScene
            // starts a Unity coroutine, so it is not patched (see the class summary).
            harmony.Patch(
                AccessTools.Method(typeof(ParsekScenario), "EnterBackupHold"),
                transpiler: new HarmonyMethod(typeof(StepRecorder), nameof(StepRecorder.TranspileNoticeSeam)));
            try
            {
                Scenario cold = Scenarios().First(s => s.Kind == "Cold");
                List<string> rows = RunScenario(cold);
                List<string> phases = rows
                    .Where(r => r.StartsWith("phase ", StringComparison.Ordinal))
                    .Select(r => r.Substring(6))
                    .ToList();
                Assert.Equal("pre-parsek-backup", phases.Last());
                Assert.DoesNotContain("game-state-recorder", phases);
                Assert.Equal("timing phase=pre-parsek-backup status=backup-hold", rows.Last());
                Assert.Contains("seam ParsekHarmony.QueueNoticeForSettledScene", rows);
                Assert.Equal("backup-hold", Token(lastPhaseTrace, "status="));
            }
            finally
            {
                PreParsekBackup.ResetHoldForTesting();
            }
        }

        [Fact]
        public void NoPatchedMethod_CallsAUnityInternalcallDirectly()
        {
            // The scan is not vacuous: the clock helpers the flow calls do read Unity directly.
            Assert.NotNull(StepRecorder.ReadRealtimeHelper);
            Assert.NotNull(StepRecorder.ReadFrameCountHelper);
            Assert.Equal(new[] { "UnityEngine.Time::get_frameCount" },
                DirectInternalcallCallees(StepRecorder.ReadFrameCountHelper));
            Assert.Equal(new[] { "UnityEngine.Time::get_realtimeSinceStartup" },
                DirectInternalcallCallees(StepRecorder.ReadRealtimeHelper));

            var offenders = new List<string>();
            int scanned = 0;
            foreach (MethodBase method in MethodsTheFactsPatch())
            {
                Assert.NotNull(method);
                scanned++;
                foreach (string callee in DirectInternalcallCallees(method))
                    offenders.Add(method.DeclaringType.Name + "." + method.Name + " -> " + callee);
            }
            Assert.True(scanned > 20, "scanned only " + scanned + " patched methods");
            Assert.True(offenders.Count == 0,
                "a patched method calls a Unity internalcall directly, so Harmony cannot compile it on "
                + "the Windows .NET host; move the call into a [MethodImpl(NoInlining)] helper: "
                + string.Join("; ", offenders));
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
            lastPhaseTrace = logLines.LastOrDefault(
                l => l.Contains("[" + LoadReconcilePolicy.LogTag + "]") && l.Contains("Load phases: "));
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

        private const BindingFlags Declared = BindingFlags.Instance | BindingFlags.Static
            | BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.DeclaredOnly;

        /// <summary>The <c>OnLoad*</c> methods every fact transpiles.</summary>
        private static IEnumerable<MethodInfo> OnLoadFlowMethods()
        {
            return typeof(ParsekScenario).GetMethods(Declared)
                .Where(m => m.Name.StartsWith("OnLoad", StringComparison.Ordinal)
                            && !m.IsGenericMethodDefinition && !m.IsAbstract);
        }

        private static MethodInfo ResolveSkippedSeam(string seam)
        {
            int sep = seam.IndexOf("::", StringComparison.Ordinal);
            Type type = typeof(ParsekScenario).Assembly.GetType(seam.Substring(0, sep), throwOnError: true);
            MethodInfo[] targets = type.GetMethods(Declared)
                .Where(m => m.Name == seam.Substring(sep + 2))
                .ToArray();
            Assert.True(targets.Length == 1, "seam " + seam + " resolves to " + targets.Length + " methods");
            return targets[0];
        }

        /// <summary>Every method any fact of this class patches (Harmony compiles each one's
        /// original). A fact that patches a new method adds it here.</summary>
        private static IEnumerable<MethodBase> MethodsTheFactsPatch()
        {
            foreach (MethodInfo method in OnLoadFlowMethods()) yield return method;
            foreach (string host in CoroutineSeamHosts)
                yield return AccessTools.Method(typeof(ParsekScenario), host);
            foreach (string seam in SkippedSeams) yield return ResolveSkippedSeam(seam);
            yield return AccessTools.Method(typeof(ParsekScenario), "EnterBackupHold");
            yield return AccessTools.Method(typeof(PreParsekBackup), nameof(PreParsekBackup.MaybeBackupOnFirstColdContact));
            yield return AccessTools.Method(typeof(ParsekFileCollector), nameof(ParsekFileCollector.RunAtLoad));
            yield return AccessTools.Method(typeof(VesselNameStore), nameof(VesselNameStore.ApplyAll));
            yield return AccessTools.Method(typeof(VesselNameStore), nameof(VesselNameStore.RunSpawnLinkBackfill));
        }

        /// <summary>The non-system internalcall methods <paramref name="method"/>'s IL calls
        /// directly (read from the IL; nothing is compiled).</summary>
        internal static List<string> DirectInternalcallCallees(MethodBase method)
        {
            var found = new List<string>();
            foreach (KeyValuePair<OpCode, object> instruction in PatchProcessor.ReadMethodBody(method))
            {
                if (!(instruction.Value is MethodBase callee)) continue;
                if ((callee.GetMethodImplementationFlags() & MethodImplAttributes.InternalCall) == 0) continue;
                // mscorlib is the one system module: its internalcalls (Math.Abs) compile anywhere.
                if (callee.Module.Assembly == typeof(object).Assembly) continue;
                found.Add(callee.DeclaringType?.FullName + "::" + callee.Name);
            }
            return found;
        }

        private void InstallSeams()
        {
            var transpiler = new HarmonyMethod(typeof(StepRecorder), nameof(StepRecorder.Transpile));
            foreach (MethodInfo method in OnLoadFlowMethods())
                harmony.Patch(method, transpiler: transpiler);

            var coroutineOnly = new HarmonyMethod(typeof(StepRecorder), nameof(StepRecorder.TranspileSeamsOnly));
            foreach (string host in CoroutineSeamHosts)
                harmony.Patch(AccessTools.Method(typeof(ParsekScenario), host), transpiler: coroutineOnly);

            foreach (string seam in SkippedSeams)
                harmony.Patch(ResolveSkippedSeam(seam),
                    prefix: new HarmonyMethod(typeof(StepRecorder), nameof(StepRecorder.SkipSeamPrefix)));
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

            /// <summary>Stands in for <c>MonoBehaviour.StartCoroutine(IEnumerator)</c> at a
            /// swapped call site (same stack shape: the receiver, then the routine).</summary>
            public static UnityEngine.Coroutine StartCoroutineSeam(UnityEngine.MonoBehaviour self, IEnumerator routine)
            {
                string name = routine == null ? "<null>" : routine.GetType().Name;
                int open = name.IndexOf('<');
                int close = name.IndexOf('>');
                if (open >= 0 && close > open) name = name.Substring(open + 1, close - open - 1);
                Steps?.Add("coroutine " + name);
                return null;
            }

            /// <summary>Stands in for <c>ParsekHarmony.QueueNoticeForSettledScene</c> in
            /// <c>EnterBackupHold</c>.</summary>
            public static void QueueNoticeSeam(string notice)
            {
                Steps?.Add("seam ParsekHarmony.QueueNoticeForSettledScene");
            }

            internal static void RunAtLoadArgsPrefix(EarlyLoadKind kind, bool coldBranch)
            {
                Steps?.Add("arg collector " + kind + " coldBranch=" + coldBranch);
            }

            public static void ApplyAllArgsPrefix(string reason)
            {
                Steps?.Add("arg names " + reason);
            }

            public static void BackfillArgsPrefix(string reason)
            {
                Steps?.Add("arg backfill " + reason);
            }

            internal static bool BackupFailsPrefix(ref PreParsekBackup.BackupAttemptOutcome __result)
            {
                Steps?.Add("seam PreParsekBackup.MaybeBackupOnFirstColdContact=Failed");
                __result = PreParsekBackup.BackupAttemptOutcome.Failed;
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

            internal static readonly MethodInfo ReadRealtimeHelper =
                typeof(ParsekScenario).GetMethod("ReadUnityRealtimeSinceStartupCore", BindingFlags.Static | BindingFlags.NonPublic);
            internal static readonly MethodInfo ReadFrameCountHelper =
                typeof(ParsekScenario).GetMethod("ReadUnityFrameCountCore", BindingFlags.Static | BindingFlags.NonPublic);
            internal static readonly MethodInfo StartCoroutineEnumerator =
                typeof(UnityEngine.MonoBehaviour).GetMethod("StartCoroutine", new[] { typeof(IEnumerator) });

            /// <summary>Swaps a call to a Unity clock helper or to <c>StartCoroutine</c> for
            /// its stand-in; false when <paramref name="instruction"/> is no such call.</summary>
            private static bool TrySwapUnitySeam(CodeInstruction instruction)
            {
                bool isCall = instruction.opcode == OpCodes.Call || instruction.opcode == OpCodes.Callvirt;
                if (!isCall || !(instruction.operand is MethodInfo callee)) return false;
                if (Equals(callee, ReadRealtimeHelper))
                {
                    instruction.operand = AccessTools.Method(typeof(StepRecorder), nameof(FixedRealtimeSinceStartup));
                    return true;
                }
                if (Equals(callee, ReadFrameCountHelper))
                {
                    instruction.operand = AccessTools.Method(typeof(StepRecorder), nameof(FixedFrameCount));
                    return true;
                }
                if (callee.DeclaringType == typeof(UnityEngine.MonoBehaviour) && callee.Name == "StartCoroutine")
                {
                    if (!Equals(callee, StartCoroutineEnumerator))
                        throw new InvalidOperationException("no stand-in for " + callee);
                    instruction.opcode = OpCodes.Call;
                    instruction.operand = AccessTools.Method(typeof(StepRecorder), nameof(StartCoroutineSeam));
                    return true;
                }
                return false;
            }

            public static IEnumerable<CodeInstruction> TranspileSeamsOnly(IEnumerable<CodeInstruction> instructions)
            {
                foreach (CodeInstruction instruction in instructions)
                {
                    TrySwapUnitySeam(instruction);
                    yield return instruction;
                }
            }

            public static IEnumerable<CodeInstruction> TranspileNoticeSeam(IEnumerable<CodeInstruction> instructions)
            {
                MethodInfo notice = AccessTools.Method(typeof(ParsekHarmony), nameof(ParsekHarmony.QueueNoticeForSettledScene));
                foreach (CodeInstruction instruction in instructions)
                {
                    if ((instruction.opcode == OpCodes.Call || instruction.opcode == OpCodes.Callvirt)
                        && Equals(instruction.operand, notice))
                        instruction.operand = AccessTools.Method(typeof(StepRecorder), nameof(QueueNoticeSeam));
                    yield return instruction;
                }
            }

            public static IEnumerable<CodeInstruction> Transpile(IEnumerable<CodeInstruction> instructions)
            {
                MethodInfo recordLiteral = AccessTools.Method(typeof(StepRecorder), nameof(RecordLiteral));
                MethodInfo recordCall = AccessTools.Method(typeof(StepRecorder), nameof(RecordCall));
                foreach (CodeInstruction instruction in instructions)
                {
                    if (TrySwapUnitySeam(instruction))
                    {
                        yield return instruction;
                        continue;
                    }
                    bool isCall = instruction.opcode == OpCodes.Call || instruction.opcode == OpCodes.Callvirt;
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
