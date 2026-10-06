using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Text.RegularExpressions;
using Xunit;

namespace Parsek.Tests
{
    /// <summary>
    /// Source gate for <see cref="LoadReconcilePolicy"/> (roadmap TA-W, todo HARNESS-TIMELINE-FUZZERS
    /// item 1): every <c>GamePersistence.LoadGame(</c> call site is a declared initiator, every node
    /// the two staging methods touch has a category, and the production sites that classify loads
    /// and arm the Discard Re-fly intent stay wired in order. A state category with no staging
    /// node, or a new load through a declared initiator, is outside what a source scan can see. <c>ParsekScenario.OnLoad</c> and <c>RevertInterceptor.DiscardReFlyHandler</c>'s real
    /// load are unreachable headlessly, so a silent unwire would leave every behavioural cell
    /// green; this file pins the WIRING.
    ///
    /// <para>Same mechanism as <c>WatchEntryAcceptanceWiringGateTests</c>: comments are stripped
    /// (and, where code shape is asserted, string-literal contents masked) through
    /// <see cref="SourceScanText"/> before any scan, so a comment or a log string naming a call
    /// cannot satisfy or trip a gate.</para>
    /// </summary>
    public class LoadReconcileWiringGateTests
    {
        private const string ScenarioPath = "ParsekScenario.cs";
        private const string InterceptorPath = "RevertInterceptor.cs";
        private const string RecorderPath = "FlightRecorder.cs";
        private const string TrimPath = "ParsekScenario.Trim.cs";

        // ---- staging nodes ----

        [Fact]
        public void EveryStagingNodeLoadedOrSavedHasACategory()
        {
            var scan = new StagingNodeScan();
            HashSet<string> loaded = scan.ScanStagingMethod("LoadRewindStagingState");
            HashSet<string> saved = scan.ScanStagingMethod("SaveRewindStagingState");

            Assert.True(scan.Problems.Count == 0,
                "load-reconcile gate: the staging methods use their ConfigNode in a way the gate cannot "
                + "resolve to a node name (resolve it to a literal or a const, or teach the gate the "
                + "helper): " + string.Join("; ", scan.Problems));

            var declared = new HashSet<string>(LoadReconcilePolicy.StagingNodeCategories.Keys, StringComparer.Ordinal);

            var unmapped = loaded.Union(saved).Where(n => !declared.Contains(n)).OrderBy(n => n).ToList();
            Assert.True(unmapped.Count == 0,
                "load-reconcile gate: the staging methods read or write node(s) with no "
                + "LoadReconcilePolicy.StagingNodeCategories entry (add the node, its LoadStateCategory "
                + "and a Decide arm for every LoadKind): " + string.Join(", ", unmapped));

            var stale = declared.Where(n => !loaded.Contains(n) || !saved.Contains(n)).OrderBy(n => n).ToList();
            Assert.True(stale.Count == 0,
                "load-reconcile gate: StagingNodeCategories names node(s) the staging methods no longer "
                + "both read and write: " + string.Join(", ", stale));

            Assert.True(loaded.SetEquals(saved),
                "load-reconcile gate: LoadRewindStagingState and SaveRewindStagingState disagree on the node set: "
                + "loadOnly=" + string.Join(",", loaded.Except(saved)) + " saveOnly=" + string.Join(",", saved.Except(loaded)));

            // The per-entry child allowlist must stay exact too: an entry nothing uses is dead.
            var unusedChildren = PerEntryChildNames.Keys.Where(k => !scan.ChildNamesSeen.Contains(k)).ToList();
            Assert.True(unusedChildren.Count == 0,
                "load-reconcile gate: PerEntryChildNames lists child name(s) no staging helper uses: "
                + string.Join(", ", unusedChildren));
        }

        // ---- load initiators ----

        [Fact]
        public void EveryLoadGameCallSiteIsADeclaredInitiator()
        {
            string root = ParsekSourceRoot();
            var callPattern = new Regex(@"\bGamePersistence\s*\.\s*LoadGame\s*\(");
            var actual = new Dictionary<string, int>(StringComparer.Ordinal);
            foreach (string path in Directory.GetFiles(root, "*.cs", SearchOption.AllDirectories))
            {
                string rel = path.Substring(root.Length)
                    .TrimStart(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar)
                    .Replace('\\', '/');
                if (rel.StartsWith("bin/", StringComparison.OrdinalIgnoreCase)
                    || rel.StartsWith("obj/", StringComparison.OrdinalIgnoreCase))
                {
                    continue;
                }
                string prepared = SourceScanText.StripCommentsAndMaskLiterals(File.ReadAllText(path));
                int count = callPattern.Matches(prepared).Count;
                if (count > 0)
                    actual[rel] = count;
            }

            var problems = new List<string>();
            foreach (var kv in actual.OrderBy(k => k.Key, StringComparer.Ordinal))
            {
                if (!LoadReconcilePolicy.KnownLoadInitiators.TryGetValue(kv.Key, out KnownLoadInitiator declared))
                    problems.Add($"{kv.Key}: {kv.Value} undeclared GamePersistence.LoadGame call(s)");
                else if (declared.CallCount != kv.Value)
                    problems.Add($"{kv.Key}: {kv.Value} call(s), table declares {declared.CallCount}");
            }
            foreach (var kv in LoadReconcilePolicy.KnownLoadInitiators)
            {
                if (!actual.ContainsKey(kv.Key))
                    problems.Add($"{kv.Key}: declared with {kv.Value.CallCount} call(s), none found");
            }

            Assert.True(problems.Count == 0,
                "load-reconcile gate: every GamePersistence.LoadGame call site must be a "
                + "LoadReconcilePolicy.KnownLoadInitiators entry with its call count and the load kinds "
                + "it produces (decide how OnLoad classifies the new load first): "
                + string.Join("; ", problems));
        }

        // ---- OnLoad order ----

        [Fact]
        public void OnLoadClassifiesBeforeStagingLoad()
        {
            string body = PreparedMethodBody(ScenarioPath, "public override void OnLoad(ConfigNode node)");

            Assert.Equal(1, Occurrences(body, "LoadReconcilePolicy.ClassifyEarly("));
            Assert.Equal(1, Occurrences(body, "LoadReconcilePolicy.ClassifyRefined("));
            Assert.Equal(1, Occurrences(body, "DiscardReFlyLoadIntent.TryConsume("));

            int detect = IndexOrFail(body, "DetectSaveFolderChange();");
            int consume = IndexOrFail(body, "DiscardReFlyLoadIntent.TryConsume(");
            int early = IndexOrFail(body, "LoadReconcilePolicy.ClassifyEarly(");
            int crew = IndexOrFail(body, "LoadCrewAndGroupState(node);");
            int staging = IndexOrFail(body, "LoadRewindStagingState(node);");
            int rpCarry = IndexOrFail(body, "RecordingStore.ReinstallRewindCarriedRewindPointsAfterLoad(");
            int listCarry = IndexOrFail(body, "RecordingStore.ReinstallRewindCarriedStagedListsAfterLoad(");
            int revert = IndexOrFail(body, "RevertDetector.Consume(");
            int isRevert = IndexOrFail(body, "ComputeIsRevertOnLoad(");
            int refined = IndexOrFail(body, "LoadReconcilePolicy.ClassifyRefined(");
            int quickloadDiscard = IndexOrFail(body, "DiscardStashedOnQuickload(");
            int revertPrune = IndexOrFail(body, "Ledger.PruneOrphanActionsAfterUT(");

            Assert.True(detect < consume && consume < early,
                "load-reconcile gate: the early classification reads initialLoadDone after "
                + "DetectSaveFolderChange and consumes the Discard Re-fly intent first");
            Assert.True(early < crew && early < staging && early < rpCarry && early < listCarry,
                "load-reconcile gate: ClassifyEarly must precede every prologue consumer "
                + "(LoadCrewAndGroupState, LoadRewindStagingState, the rewind carries)");
            Assert.True(revert < isRevert && isRevert < refined,
                "load-reconcile gate: ClassifyRefined must follow RevertDetector.Consume and the isRevert decision");
            Assert.True(refined < quickloadDiscard && refined < revertPrune,
                "load-reconcile gate: every consumer of the refined kind sits below ClassifyRefined");

            // The classifier is fed the live signals, not constants.
            string flat = Collapse(body);
            Assert.Contains("bool classifyInitialLoadDone = initialLoadDone;", flat);
            Assert.Contains("bool classifyRewinding = RewindContext.IsRewinding;", flat);
            Assert.Contains("bool classifyReFlyInvoke = RewindInvokeContext.Pending;", flat);
            Assert.Contains("DiscardReFlyLoadIntent.TryConsume( HighLogic.LoadedScene,", flat);
            Assert.Contains("LoadReconcilePolicy.ClassifyEarly( classifyInitialLoadDone, classifyRewinding, "
                + "classifyReFlyInvoke, classifyDiscardReFly)", flat);
            Assert.Contains("LoadReconcilePolicy.ClassifyRefined( earlyLoadKind, isRevert, isFlightToFlight, "
                + "utWentBackwards)", flat);
        }

        [Fact]
        public void QuickloadDiscardScienceClearReadsThePolicy()
        {
            string onLoad = Collapse(PreparedMethodBody(ScenarioPath, "public override void OnLoad(ConfigNode node)"));
            Assert.True(onLoad.Contains(
                "DiscardStashedOnQuickload(preChangeUT, loadedUT, clearPendingScience: LoadReconcilePolicy.Decide( "
                + "refinedLoadKind, LoadStateCategory.PendingScience).Action == LoadReconcileAction.Clear);"),
                "load-reconcile gate: OnLoad must pass Decide(refinedLoadKind, PendingScience) == Clear into "
                + "DiscardStashedOnQuickload");

            string discard = PreparedMethodBody(ScenarioPath, "internal static void DiscardStashedOnQuickload(");
            Assert.Equal(1, Occurrences(discard, "GameStateRecorder.PendingScienceSubjects.Clear()"));
            Assert.Contains(SourceScanText.IfConditions(discard),
                c => c.Contains("clearPendingScience") && !c.Contains("!clearPendingScience"));
            int guard = discard.IndexOf("clearPendingScience)", StringComparison.Ordinal);
            int clear = discard.IndexOf("GameStateRecorder.PendingScienceSubjects.Clear()", StringComparison.Ordinal);
            Assert.True(guard >= 0 && guard < clear,
                "load-reconcile gate: the pending-science clear must sit behind the policy flag");
        }

        // ---- the quickload-resume reconcile ----

        [Fact]
        public void PrepareQuickloadResumeCallsTheReconcile()
        {
            string body = PreparedMethodBody(RecorderPath, "private void PrepareQuickloadResumeStateIfNeeded()");
            Assert.Equal(1, Occurrences(body, "ParsekScenario.TrimAndReconcileForQuickloadResume("));
            Assert.Equal(0, Occurrences(body, "TrimRecordingTreePastUT("));
            Assert.Equal(0, Occurrences(body, "TrimRecordingPastUT("));
            int kind = IndexOrFail(body, "ParsekScenario.GetPendingQuickloadLoadKind(");
            int call = IndexOrFail(body, "ParsekScenario.TrimAndReconcileForQuickloadResume(");
            int clear = IndexOrFail(body, "ParsekScenario.ClearPendingQuickloadResumeContext();");
            Assert.True(kind < call,
                "load-reconcile gate: the resume reads the armed load kind before the reconcile");
            Assert.True(body.LastIndexOf("ParsekScenario.ClearPendingQuickloadResumeContext();", StringComparison.Ordinal) > call,
                "load-reconcile gate: the context is cleared after the reconcile read it (first clear at " + clear + ")");
            Assert.Contains("TrimAndReconcileForQuickloadResume( ActiveTree, activeRec, resumeUT, trimScope, loadKind, loadedUT, "
                + "quicksaveFacts)", Collapse(body));
            Assert.Contains("var quicksaveFacts = ParsekScenario.GetPendingQuickloadQuicksaveFacts(ActiveTree.Id);",
                Collapse(body));

            string reconcile = PreparedMethodBody(TrimPath, "internal static bool TrimAndReconcileForQuickloadResume(");
            int plan = IndexOrFail(reconcile, "BuildAbandonedFuturePlan(");
            int treeTrim = IndexOrFail(reconcile, "TrimRecordingTreePastUT(");
            int recTrim = IndexOrFail(reconcile, "TrimRecordingPastUT(");
            Assert.True(plan < treeTrim && plan < recTrim,
                "load-reconcile gate: the abandoned-future plan is taken before the trim cuts the payload");
            string flat = Collapse(reconcile);
            Assert.Contains("ShouldReconcileAtResume(loadKind, LoadStateCategory.AbandonedFutureEndStates)", flat);
            Assert.Contains("ShouldReconcileAtResume(loadKind, LoadStateCategory.AbandonedFutureEvents)", flat);
            Assert.Contains("ShouldReconcileAtResume(loadKind, LoadStateCategory.AbandonedFutureLedgerRows)", flat);
            // The ledger step reads the end-state step's cleared set (the KerbalAssignment rule),
            // so the end states are cleared first.
            int endStates = IndexOrFail(reconcile, "ClearAbandonedFutureEndStates(");
            int events = IndexOrFail(reconcile, "PurgeAbandonedFutureEvents(");
            int rows = IndexOrFail(reconcile, "RetireAbandonedFutureLedgerRows(");
            int recalc = IndexOrFail(reconcile, "LedgerOrchestrator.RecalculateAndPatchForCurrentTimelineIfFutureActions(");
            Assert.True(treeTrim < endStates && endStates < events && events < rows && rows < recalc,
                "load-reconcile gate: trim, end states, events, ledger rows, then the current-timeline recalculation");
        }

        [Fact]
        public void RestoreCapturesTheQuicksaveFactsBeforeAnythingChangesTheLoadedTree()
        {
            // The committed-history discriminator must read the quicksave as written: before the
            // sidecar hydration, the committed-copy decision and its salvage, the stale-epoch
            // keep, the pending-tree salvage and the same-id refresh, and before the detach of
            // the committed copy.
            string body = PreparedMethodBody(ScenarioPath, "internal static bool TryRestoreActiveTreeNode(");
            Assert.Equal(1, Occurrences(body, "CaptureQuicksaveTreeFacts("));
            int load = IndexOrFail(body, "RecordingTree.Load(");
            int capture = IndexOrFail(body, "CaptureQuicksaveTreeFacts(");
            int hydrate = IndexOrFail(body, "RecordingStore.LoadRecordingFiles(");
            int decide = IndexOrFail(body, "ResolveCommittedCopyRestore(");
            int keep = IndexOrFail(body, "ShouldKeepPendingTreeAfterHydrationFailure(");
            int salvage = IndexOrFail(body, "RestoreHydrationFailedRecordingsFromPendingTree(");
            int splice = IndexOrFail(body, "SpliceMissingCommittedRecordingsIntoLoadedTree(");
            int detach = IndexOrFail(body, "RecordingStore.RemoveCommittedTreeById(");
            int clearAttempt = IndexOrFail(body, "ClearCommittedTreeRestoreAttemptAfterDetach(");
            Assert.True(load < capture && capture < hydrate && hydrate < decide && decide < keep
                && keep < salvage && salvage < splice && splice < detach && detach < clearAttempt,
                "load-reconcile gate: TryRestoreActiveTreeNode must capture the quicksave facts right after "
                + "loading the node, before hydration; decide the committed-copy rule after hydration and "
                + "before the keep, the salvage, the splice and the detach; clear the restore attempt after the detach");
            Assert.Contains("CaptureQuicksaveTreeFacts( tree, CollectQuicksaveCommittedRecordingIds(node), "
                + "CollectQuicksaveCommittedTreeIds(node));", Collapse(body));
            // The rule replaces the stale-epoch keep: the keep is gated on it, and the restore
            // attempt is cleared only on it.
            Assert.Contains("if (committedCopyAction != CommittedCopyRestoreAction.ResumeFromQuicksave "
                + "&& ShouldKeepPendingTreeAfterHydrationFailure(tree, staleEpochHydrationFailures))", Collapse(body));
            Assert.Contains("if (committedCopyAction == CommittedCopyRestoreAction.ResumeFromQuicksave) "
                + "ClearCommittedTreeRestoreAttemptAfterDetach(tree);", Collapse(body));

            // Both OnLoad call sites pass the prologue's load kind and whether the load lands in
            // FLIGHT (the defaults model an F9 in flight, for tests only).
            string onLoad = Collapse(PreparedMethodBody(ScenarioPath, "public override void OnLoad(ConfigNode node)"));
            Assert.Equal(2, Occurrences(onLoad, "TryRestoreActiveTreeNode("));
            Assert.Contains("TryRestoreActiveTreeNode( node, earlyLoadKind, HighLogic.LoadedScene == GameScenes.FLIGHT)", onLoad);
            Assert.Contains("TryRestoreActiveTreeNode( node, EarlyLoadKind.Cold, HighLogic.LoadedScene == GameScenes.FLIGHT)", onLoad);

            string arm = PreparedMethodBody(ScenarioPath, "internal static void ConfigurePendingQuickloadResumeContext(");
            Assert.Contains("QuicksaveFacts = quicksaveFacts,", Collapse(arm));
        }

        [Fact]
        public void ResumeContextIsArmedWithTheLoadKind()
        {
            string onLoad = Collapse(PreparedMethodBody(ScenarioPath, "public override void OnLoad(ConfigNode node)"));
            Assert.Equal(2, Occurrences(onLoad, "ConfigurePendingQuickloadResumeContext("));
            Assert.Contains("ConfigurePendingQuickloadResumeContext( RecordingStore.PendingTree, refinedLoadKind, "
                + "planetariumReady ? loadedUT : double.NaN);", onLoad);
            Assert.Contains("ConfigurePendingQuickloadResumeContext(RecordingStore.PendingTree, LoadKind.Cold);", onLoad);

            string root = ParsekSourceRoot();
            var armFiles = new List<string>();
            foreach (string path in Directory.GetFiles(root, "*.cs", SearchOption.AllDirectories))
            {
                string rel = path.Substring(root.Length)
                    .TrimStart(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar)
                    .Replace('\\', '/');
                if (rel.StartsWith("bin/", StringComparison.OrdinalIgnoreCase)
                    || rel.StartsWith("obj/", StringComparison.OrdinalIgnoreCase))
                {
                    continue;
                }
                string prepared = SourceScanText.StripCommentsAndMaskLiterals(File.ReadAllText(path));
                if (prepared.Contains("ConfigurePendingQuickloadResumeContext("))
                    armFiles.Add(rel);
            }
            Assert.Equal(new[] { "ParsekScenario.cs" }, armFiles);
        }

        // ---- the Discard Re-fly intent ----

        [Fact]
        public void RevertInterceptorArmsTheDiscardIntentAfterLoadGame()
        {
            string body = PreparedMethodBody(InterceptorPath, "internal static void DiscardReFlyHandler(");

            Assert.Equal(1, Occurrences(body, "DiscardReFlyLoadIntent.Arm("));
            int loadCall = IndexOrFail(body, "TryLoadGameAndAssign(");
            int loadFailed = IndexOrFail(body, "if (!loadSucceeded)");
            int arm = IndexOrFail(body, "DiscardReFlyLoadIntent.Arm(");
            int dispatch = IndexOrFail(body, "DispatchScene(");
            int clear = IndexOrFail(body, "DiscardReFlyLoadIntent.Clear(");

            Assert.True(loadCall < loadFailed && loadFailed < arm,
                "load-reconcile gate: the intent is armed only after the load-failure return");
            Assert.True(arm < dispatch && dispatch < clear,
                "load-reconcile gate: the intent is armed before the scene dispatch and cleared after it fails");
            Assert.Contains("if (!dispatched) DiscardReFlyLoadIntent.Clear(", Collapse(body));
        }

        [Fact]
        public void DiscardIntentHasOneArmSiteAndOneReader()
        {
            string root = ParsekSourceRoot();
            var armSites = new List<string>();
            var readSites = new List<string>();
            foreach (string path in Directory.GetFiles(root, "*.cs", SearchOption.AllDirectories))
            {
                string rel = path.Substring(root.Length)
                    .TrimStart(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar)
                    .Replace('\\', '/');
                if (rel.StartsWith("bin/", StringComparison.OrdinalIgnoreCase)
                    || rel.StartsWith("obj/", StringComparison.OrdinalIgnoreCase))
                {
                    continue;
                }
                string prepared = SourceScanText.StripCommentsAndMaskLiterals(File.ReadAllText(path));
                for (int i = 0; i < Occurrences(prepared, "DiscardReFlyLoadIntent.Arm("); i++)
                    armSites.Add(rel);
                for (int i = 0; i < Occurrences(prepared, "DiscardReFlyLoadIntent.TryConsume("); i++)
                    readSites.Add(rel);
            }

            Assert.Equal(new[] { "RevertInterceptor.cs" }, armSites);
            Assert.Equal(new[] { "ParsekScenario.cs" }, readSites);
        }

        // ---- helpers ----

        // ConfigNode members that address a child node by name; their first argument is that name.
        private static readonly HashSet<string> NodeChildAccessors = new HashSet<string>(StringComparer.Ordinal)
        {
            "AddNode", "GetNode", "GetNodes", "HasNode", "RemoveNode", "RemoveNodes", "SetNode", "TryGetNode",
        };

        // Child names one level BELOW a staging node (an entry of a staged list), never a node of
        // the scenario node itself. Each must be used by a staging helper.
        private static readonly Dictionary<string, string> PerEntryChildNames =
            new Dictionary<string, string>(StringComparer.Ordinal)
            {
                { "POINT", "one rewind point under REWIND_POINTS (LoadStagingList childName)" },
                { "ENTRY", "one row under RECORDING_SUPERSEDES / RECORDING_REWIND_RETIREMENTS / LEDGER_TOMBSTONES (LoadStagingList childName)" },
            };

        /// <summary>
        /// Walks every use of a staging method's <c>ConfigNode</c> parameter. A child accessor
        /// (<see cref="NodeChildAccessors"/>) on it contributes its first argument, resolved from a
        /// string literal, a const of the scanning type, or a <c>Type.Const</c> member access. A
        /// call that passes the node on is followed ONE level: a static helper declared on
        /// <c>ParsekScenario</c>, or <c>member.SaveInto(node)</c> on a scenario field / property
        /// (scanned in the member type's own source). Anything else - an unknown member of the
        /// node, an argument that does not resolve, the node passed to an unknown method or on
        /// again from a helper, the node used outside a call - is a problem, never a silent skip.
        /// </summary>
        private sealed class StagingNodeScan
        {
            internal readonly List<string> Problems = new List<string>();
            internal readonly HashSet<string> ChildNamesSeen = new HashSet<string>(StringComparer.Ordinal);
            private readonly Dictionary<string, Source> sources = new Dictionary<string, Source>(StringComparer.Ordinal);
            private readonly List<Source> scenarioSources;

            internal sealed class Source
            {
                internal string Name;
                internal string Stripped;
                internal string Masked;
            }

            private sealed class Method
            {
                internal Source File;
                internal int Open;
                internal List<string> Params;
                internal string Where;
            }

            private delegate string Resolver(string expr, out string error);

            internal StagingNodeScan()
            {
                scenarioSources = Directory.GetFiles(ParsekSourceRoot(), "ParsekScenario*.cs", SearchOption.TopDirectoryOnly)
                    .Select(LoadSource)
                    .Where(src => Regex.IsMatch(src.Masked, @"\bpartial\s+class\s+ParsekScenario\b"))
                    .ToList();
                Assert.True(scenarioSources.Count > 0, "load-reconcile gate: no ParsekScenario source found");
            }

            internal HashSet<string> ScanStagingMethod(string methodName)
            {
                var names = new HashSet<string>(StringComparer.Ordinal);
                Method m = FindMethod(scenarioSources, methodName);
                if (m == null)
                {
                    Problems.Add(methodName + ": declaration not found (or not unique)");
                    return names;
                }
                if (m.Params.Count != 1)
                {
                    Problems.Add(methodName + ": expected exactly one (ConfigNode) parameter");
                    return names;
                }
                Resolver resolver = (string expr, out string error) =>
                    ResolveConst(expr, typeof(ParsekScenario), out error);
                ScanNodeUses(m, m.Params[0], typeof(ParsekScenario), resolver, topLevel: true, names: names);
                return names;
            }

            private void ScanNodeUses(
                Method m, string param, Type context, Resolver resolve, bool topLevel, HashSet<string> names)
            {
                string masked = m.File.Masked;
                int end = m.Open + SourceScanText.BraceMatchedBlock(masked, m.Open).Length;
                var use = new Regex(@"(?<![\w.])" + Regex.Escape(param) + @"(?!\w)");
                for (Match u = use.Match(masked, m.Open); u.Success && u.Index < end; u = u.NextMatch())
                {
                    int after = SkipSpace(masked, u.Index + param.Length);
                    if (after < masked.Length && masked[after] == '.')
                    {
                        Match member = Regex.Match(masked.Substring(after + 1), @"^\s*(\w+)\s*");
                        string name = member.Groups[1].Value;
                        int paren = after + 1 + member.Length;
                        if (!NodeChildAccessors.Contains(name))
                        {
                            Problems.Add($"{m.Where}: {param}.{name} is not a child-node accessor the gate knows");
                            continue;
                        }
                        if (paren >= masked.Length || masked[paren] != '(')
                        {
                            Problems.Add($"{m.Where}: {param}.{name} is not called");
                            continue;
                        }
                        List<string> args = CallArgs(m.File, paren);
                        if (args.Count == 0)
                        {
                            Problems.Add($"{m.Where}: {param}.{name}() has no name argument");
                            continue;
                        }
                        string value = resolve(args[0], out string error);
                        if (value == null)
                            Problems.Add($"{m.Where}: {param}.{name}({args[0]}): {error}");
                        else
                            names.Add(value);
                        continue;
                    }

                    if (Regex.IsMatch(masked.Substring(after, Math.Min(16, masked.Length - after)), @"^[!=]=\s*null"))
                        continue;

                    int open = EnclosingCallParen(masked, m.Open, u.Index);
                    if (open < 0)
                    {
                        Problems.Add($"{m.Where}: '{param}' used outside a call argument list");
                        continue;
                    }
                    Match calleeMatch = Regex.Match(masked.Substring(m.Open, open - m.Open),
                        @"([A-Za-z_][\w.]*)\s*(?:<[^<>()]*>)?\s*$");
                    string callee = calleeMatch.Success ? calleeMatch.Groups[1].Value : "<none>";
                    if (callee == "nameof")
                        continue;
                    List<string> callArgs = CallArgs(m.File, open);
                    int index = callArgs.FindIndex(a => a == param);
                    if (index < 0)
                    {
                        Problems.Add($"{m.Where}: '{param}' passed to {callee} inside an expression");
                        continue;
                    }
                    if (!topLevel)
                    {
                        Problems.Add($"{m.Where}: '{param}' passed on to {callee} below the one helper level the gate follows");
                        continue;
                    }

                    Resolver callerResolve = resolve;
                    if (callee.EndsWith(".SaveInto", StringComparison.Ordinal))
                    {
                        string receiver = callee.Substring(0, callee.Length - ".SaveInto".Length);
                        Type memberType = MemberType(context, receiver);
                        if (memberType == null)
                        {
                            Problems.Add($"{m.Where}: {callee}({param}): cannot resolve the type of '{receiver}'");
                            continue;
                        }
                        Method saveInto = FindMethod(TypeSources(memberType), "SaveInto");
                        if (saveInto == null || saveInto.Params.Count != 1)
                        {
                            Problems.Add($"{m.Where}: {memberType.Name}.SaveInto(ConfigNode) not found (or not unique)");
                            continue;
                        }
                        ScanHelper(saveInto, 0, callArgs, callerResolve, memberType, names);
                    }
                    else if (callee.IndexOf('.') < 0)
                    {
                        Method helper = FindMethod(scenarioSources, callee);
                        if (helper == null || index >= helper.Params.Count)
                        {
                            Problems.Add($"{m.Where}: '{param}' passed to {callee}, which the gate cannot find on ParsekScenario");
                            continue;
                        }
                        ScanHelper(helper, index, callArgs, callerResolve, typeof(ParsekScenario), names);
                    }
                    else
                    {
                        Problems.Add($"{m.Where}: '{param}' passed to {callee}, which the gate does not know");
                    }
                }
            }

            private void ScanHelper(
                Method helper, int nodeIndex, List<string> callArgs, Resolver callerResolve,
                Type helperContext, HashSet<string> names)
            {
                Resolver resolve = (string expr, out string error) =>
                {
                    int i = helper.Params.IndexOf(expr.Trim());
                    if (i >= 0)
                    {
                        if (i >= callArgs.Count)
                        {
                            error = "helper parameter '" + expr + "' has no argument at the call site";
                            return null;
                        }
                        return callerResolve(callArgs[i], out error);
                    }
                    return ResolveConst(expr, helperContext, out error);
                };
                string nodeParam = helper.Params[nodeIndex];
                ScanNodeUses(helper, nodeParam, helperContext, resolve, topLevel: false, names: names);

                // Child accessors on any OTHER node inside the helper address an entry of a staged
                // node; a name that reaches them from the call site must be a declared child name.
                string masked = helper.File.Masked;
                int end = helper.Open + SourceScanText.BraceMatchedBlock(masked, helper.Open).Length;
                var childUse = new Regex(@"(?<![\w.])(\w+)\s*\.\s*(\w+)\s*\(");
                for (Match c = childUse.Match(masked, helper.Open); c.Success && c.Index < end; c = c.NextMatch())
                {
                    if (c.Groups[1].Value == nodeParam || !NodeChildAccessors.Contains(c.Groups[2].Value))
                        continue;
                    List<string> args = CallArgs(helper.File, c.Index + c.Length - 1);
                    if (args.Count == 0 || helper.Params.IndexOf(args[0]) < 0)
                        continue;
                    string value = resolve(args[0], out string error);
                    if (value == null)
                        Problems.Add($"{helper.Where}: {c.Groups[1].Value}.{c.Groups[2].Value}({args[0]}): {error}");
                    else if (!PerEntryChildNames.ContainsKey(value))
                        Problems.Add($"{helper.Where}: child name '{value}' is not a declared per-entry child name");
                    else
                        ChildNamesSeen.Add(value);
                }
            }

            private static string ResolveConst(string expr, Type context, out string error)
            {
                error = null;
                string e = (expr ?? "").Trim();
                Match literal = Regex.Match(e, "^\"([^\"\\\\]*)\"$");
                if (literal.Success)
                    return literal.Groups[1].Value;

                Match member = Regex.Match(e, @"^(\w+)\.(\w+)$");
                if (member.Success)
                {
                    Type type = typeof(LoadReconcilePolicy).Assembly.GetType("Parsek." + member.Groups[1].Value)
                        ?? context.GetNestedType(member.Groups[1].Value, BindingFlags.Public | BindingFlags.NonPublic);
                    if (type == null)
                    {
                        error = "cannot resolve type '" + member.Groups[1].Value + "'";
                        return null;
                    }
                    return ConstValue(type, member.Groups[2].Value, out error);
                }

                if (Regex.IsMatch(e, @"^\w+$"))
                    return ConstValue(context, e, out error);

                error = "'" + e + "' is not a string literal or a const";
                return null;
            }

            private static string ConstValue(Type type, string name, out string error)
            {
                error = null;
                FieldInfo field = type.GetField(name,
                    BindingFlags.Static | BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.FlattenHierarchy);
                if (field == null || !field.IsLiteral || field.FieldType != typeof(string))
                {
                    error = "'" + name + "' is not a string const of " + type.Name;
                    return null;
                }
                return (string)field.GetRawConstantValue();
            }

            private static Type MemberType(Type context, string name)
            {
                const BindingFlags all = BindingFlags.Instance | BindingFlags.Static
                    | BindingFlags.Public | BindingFlags.NonPublic;
                FieldInfo field = context.GetField(name, all);
                if (field != null) return field.FieldType;
                PropertyInfo property = context.GetProperty(name, all);
                return property?.PropertyType;
            }

            private List<Source> TypeSources(Type type)
            {
                var found = new List<Source>();
                var declares = new Regex(@"\b(?:class|struct)\s+" + Regex.Escape(type.Name) + @"\b");
                foreach (string path in Directory.GetFiles(ParsekSourceRoot(), "*.cs", SearchOption.AllDirectories))
                {
                    string rel = path.Substring(ParsekSourceRoot().Length).TrimStart('/', '\\').Replace('\\', '/');
                    if (rel.StartsWith("bin/", StringComparison.OrdinalIgnoreCase)
                        || rel.StartsWith("obj/", StringComparison.OrdinalIgnoreCase))
                        continue;
                    if (File.ReadAllText(path).IndexOf(type.Name, StringComparison.Ordinal) < 0)
                        continue;
                    Source src = LoadSource(path);
                    if (declares.IsMatch(src.Masked))
                        found.Add(src);
                }
                return found;
            }

            private Source LoadSource(string path)
            {
                if (sources.TryGetValue(path, out Source cached))
                    return cached;
                string stripped = SourceScanText.StripCSharpComments(File.ReadAllText(path));
                var src = new Source
                {
                    Name = Path.GetFileName(path),
                    Stripped = stripped,
                    Masked = SourceScanText.MaskStringLiteralContents(stripped),
                };
                sources[path] = src;
                return src;
            }

            // The unique declaration of `name` (a method body follows its parameter list).
            private static Method FindMethod(IEnumerable<Source> files, string name)
            {
                Method found = null;
                var decl = new Regex(@"\b" + Regex.Escape(name) + @"\s*(?:<[^<>()]*>)?\s*\(");
                foreach (Source src in files)
                {
                    foreach (Match d in decl.Matches(src.Masked))
                    {
                        int open = d.Index + d.Length - 1;
                        int close = MatchingParen(src.Masked, open);
                        if (close < 0) continue;
                        int brace = SkipSpace(src.Masked, close + 1);
                        if (brace >= src.Masked.Length || src.Masked[brace] != '{') continue;
                        string before = src.Masked.Substring(Math.Max(0, d.Index - 200), Math.Min(200, d.Index));
                        if (!Regex.IsMatch(before, @"(?:private|internal|public|protected|static|void|\w>)\s+[\w<>\[\],. ]*$"))
                            continue;
                        if (found != null)
                            return null;
                        found = new Method
                        {
                            File = src,
                            Open = brace,
                            Params = SplitTopLevel(src.Masked, open, close, trackAngles: true)
                                .Select(p => Regex.Replace(src.Masked.Substring(p.Item1, p.Item2 - p.Item1), @"=.*$", "").Trim())
                                .Where(p => p.Length > 0)
                                .Select(p => Regex.Match(p, @"(\w+)$").Groups[1].Value)
                                .ToList(),
                            Where = src.Name + ":" + name,
                        };
                    }
                }
                return found;
            }

            // Argument texts (comment-stripped, literals intact) of the call whose '(' is at `open`.
            private static List<string> CallArgs(Source src, int open)
            {
                int close = MatchingParen(src.Masked, open);
                if (close < 0) return new List<string>();
                return SplitTopLevel(src.Masked, open, close, trackAngles: false)
                    .Select(r => src.Stripped.Substring(r.Item1, r.Item2 - r.Item1).Trim())
                    .Where(a => a.Length > 0)
                    .ToList();
            }

            private static List<Tuple<int, int>> SplitTopLevel(string masked, int open, int close, bool trackAngles)
            {
                var parts = new List<Tuple<int, int>>();
                int depth = 0;
                int start = open + 1;
                for (int i = open + 1; i < close; i++)
                {
                    char ch = masked[i];
                    if (ch == '(' || ch == '[' || ch == '{' || (trackAngles && ch == '<')) depth++;
                    else if (ch == ')' || ch == ']' || ch == '}' || (trackAngles && ch == '>')) depth--;
                    else if (ch == ',' && depth == 0)
                    {
                        parts.Add(Tuple.Create(start, i));
                        start = i + 1;
                    }
                }
                parts.Add(Tuple.Create(start, close));
                return parts;
            }

            private static int MatchingParen(string masked, int open)
            {
                int depth = 0;
                for (int i = open; i < masked.Length; i++)
                {
                    if (masked[i] == '(') depth++;
                    else if (masked[i] == ')' && --depth == 0) return i;
                }
                return -1;
            }

            // The '(' of the innermost call whose argument list contains `index`, or -1 when a
            // statement or block boundary comes first.
            private static int EnclosingCallParen(string masked, int lowerBound, int index)
            {
                int depth = 0;
                for (int i = index - 1; i > lowerBound; i--)
                {
                    char ch = masked[i];
                    if (ch == ')' || ch == ']') depth++;
                    else if (ch == '[' && depth > 0) depth--;
                    else if (ch == '(')
                    {
                        if (depth == 0) return i;
                        depth--;
                    }
                    else if ((ch == ';' || ch == '{' || ch == '}') && depth == 0)
                        return -1;
                }
                return -1;
            }

            private static int SkipSpace(string text, int i)
            {
                while (i < text.Length && char.IsWhiteSpace(text[i])) i++;
                return i;
            }
        }

        private static string PreparedMethodBody(string relPath, string signatureFragment)
        {
            string prepared = SourceScanText.StripCommentsAndMaskLiterals(ReadParsekSource(relPath));
            int open = MethodOpenBrace(prepared, relPath, signatureFragment);
            return SourceScanText.BraceMatchedBlock(prepared, open);
        }

        private static int MethodOpenBrace(string prepared, string relPath, string signatureFragment)
        {
            int sigIdx = prepared.IndexOf(signatureFragment, StringComparison.Ordinal);
            Assert.True(sigIdx >= 0,
                "load-reconcile gate: signature not found in " + relPath + ": " + signatureFragment);
            Assert.True(
                prepared.IndexOf(signatureFragment, sigIdx + 1, StringComparison.Ordinal) < 0,
                "load-reconcile gate: signature is ambiguous in " + relPath + ": " + signatureFragment);
            int open = prepared.IndexOf('{', sigIdx);
            Assert.True(open >= 0, "load-reconcile gate: no body for " + signatureFragment);
            return open;
        }

        private static int IndexOrFail(string body, string needle)
        {
            int idx = body.IndexOf(needle, StringComparison.Ordinal);
            Assert.True(idx >= 0, "load-reconcile gate: '" + needle + "' not found");
            return idx;
        }

        private static int Occurrences(string text, string needle)
        {
            int count = 0;
            int idx = 0;
            while ((idx = text.IndexOf(needle, idx, StringComparison.Ordinal)) >= 0)
            {
                count++;
                idx += needle.Length;
            }
            return count;
        }

        private static string Collapse(string text)
        {
            return Regex.Replace(text, @"\s+", " ");
        }

        private static string ParsekSourceRoot()
        {
            string repo = Path.GetFullPath(Path.Combine(
                AppDomain.CurrentDomain.BaseDirectory, "..", "..", "..", "..", ".."));
            string root = Path.Combine(repo, "Source", "Parsek");
            Assert.True(Directory.Exists(root), "Parsek source root not found at " + root);
            return root;
        }

        private static string ReadParsekSource(string relPath)
        {
            string path = Path.Combine(ParsekSourceRoot(), relPath.Replace('/', Path.DirectorySeparatorChar));
            Assert.True(File.Exists(path), "Source file not found at " + path);
            return File.ReadAllText(path);
        }
    }
}
