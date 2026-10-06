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
    /// item 1): every load path and every staged state category has a decision in the table, and
    /// the production sites that classify loads and arm the Discard Re-fly intent stay wired in
    /// order. <c>ParsekScenario.OnLoad</c> and <c>RevertInterceptor.DiscardReFlyHandler</c>'s real
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

        // ---- staging nodes ----

        [Fact]
        public void EveryStagingNodeLoadedOrSavedHasACategory()
        {
            HashSet<string> loaded = StagingNodeNames("private void LoadRewindStagingState(ConfigNode node)");
            HashSet<string> saved = StagingNodeNames("private void SaveRewindStagingState(ConfigNode node)");

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

        private static HashSet<string> StagingNodeNames(string signature)
        {
            string body = CommentStrippedMethodBody(ScenarioPath, signature);
            var names = new HashSet<string>(StringComparer.Ordinal);

            // Literal node names: every all-caps literal with an underscore (child names such as
            // "POINT" / "ENTRY" carry none).
            foreach (Match m in Regex.Matches(body, "\"([A-Z][A-Z0-9]*(?:_[A-Z0-9]+)+)\""))
                names.Add(m.Groups[1].Value);

            // Singleton node names: Type.NodeName, resolved to the constant's value.
            Assembly parsek = typeof(LoadReconcilePolicy).Assembly;
            foreach (Match m in Regex.Matches(body, @"\b([A-Z][A-Za-z0-9_]*)\.NodeName\b"))
            {
                Type type = parsek.GetType("Parsek." + m.Groups[1].Value);
                Assert.True(type != null, "load-reconcile gate: cannot resolve type " + m.Groups[1].Value);
                FieldInfo field = type.GetField("NodeName",
                    BindingFlags.Static | BindingFlags.Public | BindingFlags.NonPublic);
                Assert.True(field != null && field.IsLiteral,
                    "load-reconcile gate: " + m.Groups[1].Value + ".NodeName is not a const");
                names.Add((string)field.GetRawConstantValue());
            }

            Assert.True(names.Count > 0, "load-reconcile gate: no node names found in " + signature);
            return names;
        }

        private static string PreparedMethodBody(string relPath, string signatureFragment)
        {
            string prepared = SourceScanText.StripCommentsAndMaskLiterals(ReadParsekSource(relPath));
            int open = MethodOpenBrace(prepared, relPath, signatureFragment);
            return SourceScanText.BraceMatchedBlock(prepared, open);
        }

        // Comments blanked, literal contents kept: the brace walk runs over the masked form (same
        // length, same indices) and the body is cut from the unmasked one.
        private static string CommentStrippedMethodBody(string relPath, string signatureFragment)
        {
            string source = ReadParsekSource(relPath);
            string stripped = SourceScanText.StripCSharpComments(source);
            string prepared = SourceScanText.MaskStringLiteralContents(stripped);
            int open = MethodOpenBrace(prepared, relPath, signatureFragment);
            string masked = SourceScanText.BraceMatchedBlock(prepared, open);
            return stripped.Substring(open, masked.Length);
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
