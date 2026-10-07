using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.RegularExpressions;
using Xunit;

namespace Parsek.Tests
{
    /// <summary>
    /// Source gate for the in-session staged-list handoff (roadmap TA-1): where
    /// <c>ParsekScenario</c> captures it (OnDestroy, the OnAwake fallback), where OnLoad applies
    /// it (step A after the plain-rewind carries and before the rewind branch; step B after the
    /// active and pending tree restores and before anything reads the rewind-point list), every
    /// drop reason has a producer, and both Re-Fly discard helpers prune the rows that name the
    /// attempt they delete (owner ruling OQ-1). OnLoad, OnDestroy and OnAwake are unreachable
    /// headlessly, so the behavioural cells in <see cref="InSessionStagedListsCarryTests"/> call the
    /// same methods in this order; this file pins the order. Comments are stripped and string
    /// literals masked before any scan (<see cref="SourceScanText"/>).
    /// </summary>
    public class InSessionHandoffWiringGateTests
    {
        private const string ScenarioPath = "ParsekScenario.cs";
        private const string HandoffPartialPath = "ParsekScenario.InSessionHandoff.cs";
        private const string DiscardPath = "MergeDialog.ReFlyDiscard.cs";

        private const string StepA = "ApplyInSessionStagedStateHandoffStepA(";
        private const string StepB = "ApplyInSessionStagedStateHandoffStepB(";

        [Fact]
        public void OnLoadAppliesTheHandoffInOrder()
        {
            string body = PreparedMethodBody(ScenarioPath, "public override void OnLoad(ConfigNode node)");

            Assert.Equal(1, Occurrences(body, StepA));
            Assert.Equal(1, Occurrences(body, StepB));
            Assert.Contains("ApplyInSessionStagedStateHandoffStepA(earlyLoadKind);", Collapse(body));

            int early = IndexOrFail(body, "LoadReconcilePolicy.ClassifyEarly(");
            int describe = IndexOrFail(body, "InSessionStagedStateHandoff.DescribeForClassification()");
            int staging = IndexOrFail(body, "LoadRewindStagingState(node);");
            int rpCarry = IndexOrFail(body, "RecordingStore.ReinstallRewindCarriedRewindPointsAfterLoad(");
            int listCarry = IndexOrFail(body, "RecordingStore.ReinstallRewindCarriedStagedListsAfterLoad(");
            int stepA = IndexOrFail(body, StepA);
            int rewindBranch = IndexOrFail(body, "HandleRewindOnLoad(node, recordings);");
            int externalFiles = IndexOrFail(body, "LoadExternalFiles();");

            Assert.True(describe < stepA && early < stepA,
                "in-session handoff gate: the load is classified (and the handoff's presence read for the "
                + "classification line) before step A consumes it");
            Assert.True(staging < stepA && rpCarry < stepA && listCarry < stepA,
                "in-session handoff gate: step A must follow LoadRewindStagingState and both plain-rewind carries");
            Assert.True(stepA < rewindBranch && stepA < externalFiles,
                "in-session handoff gate: step A runs in the prologue, before the rewind branch and the cold path");

            int activeRestore = IndexOrFail(body, "TryRestoreActiveTreeNode(");
            int pendingRestore = IndexOrFail(body, "TryRestorePendingTreeNode(");
            int stepB = IndexOrFail(body, StepB);
            int revertDetect = IndexOrFail(body, "RevertDetector.Consume(");
            int autoCommit = IndexOrFail(body, "AutoCommitPendingTreeOutsideFlight(");
            int reFlyDispatch = IndexOrFail(body, "DispatchRewindPostLoadIfPending(");
            int finisher = IndexOrFail(body, "MergeJournalOrchestrator.RunFinisher(");
            int sweep = IndexOrFail(body, "LoadTimeSweep.Run();");

            Assert.True(rewindBranch < activeRestore && activeRestore < pendingRestore && pendingRestore < stepB,
                "in-session handoff gate: step B follows the in-session active and pending tree restores, so a "
                + "resumed tree has been detached from the committed set");
            Assert.True(stepB < revertDetect && stepB < autoCommit && stepB < reFlyDispatch
                && stepB < finisher && stepB < sweep,
                "in-session handoff gate: step B runs before the revert branch, the scene-exit auto-commit, the "
                + "Re-Fly dispatch, the journal finisher and LoadTimeSweep.Run read the rewind-point list");
            int coldBranch = IndexOrFail(body, "initialLoadDone = true;");
            Assert.True(coldBranch > stepB, "in-session handoff gate: step B lives in the in-session branch");

            // Both classification lines carry the handoff token.
            Assert.Equal(2, Occurrences(Collapse(body), "classifyDiscardReFlyTarget, classifyHandoff);"));
        }

        [Fact]
        public void TheInertEarlyReturnDropsTheHandoff()
        {
            string body = PreparedMethodBody(ScenarioPath, "public override void OnLoad(ConfigNode node)");
            int inert = IndexOrFail(body, "ParsekGameModeGate.CheckInert(");
            int drop = IndexOrFail(body, "InSessionStagedStateHandoff.Drop(InSessionStagedStateHandoff.DropInertGameMode);");
            int firstReturn = IndexOrFail(body, "return;");
            Assert.True(inert < drop && drop < firstReturn,
                "in-session handoff gate: the inert game-mode early return consumes (drops) the handoff");
        }

        [Fact]
        public void OnDestroyCapturesTheHandoffWhileStillTheInstance()
        {
            string body = PreparedMethodBody(ScenarioPath, "public void OnDestroy()");
            Assert.Equal(1, Occurrences(body, "CaptureInSessionStagedStateHandoff("));
            int guard = IndexOrFail(body, "if (ReferenceEquals(s_instance, this))");
            int capture = IndexOrFail(body,
                "CaptureInSessionStagedStateHandoff(InSessionStagedStateHandoff.CaptureReasonOnDestroy);");
            int clear = IndexOrFail(body, "s_instance = null;");
            Assert.True(guard < capture && capture < clear,
                "in-session handoff gate: OnDestroy captures inside the Instance guard, before dropping Instance");
        }

        [Fact]
        public void OnAwakeCapturesFromAStillRegisteredPredecessor()
        {
            string body = PreparedMethodBody(ScenarioPath, "public override void OnAwake()");
            Assert.Equal(1, Occurrences(body, "CaptureInSessionStagedStateHandoff("));
            int guard = IndexOrFail(body, "!ReferenceEquals(predecessor, this)");
            int capture = IndexOrFail(body,
                "predecessor.CaptureInSessionStagedStateHandoff(InSessionStagedStateHandoff.CaptureReasonOnAwake);");
            int publish = IndexOrFail(body, "s_instance = this;");
            Assert.True(guard < capture && capture < publish,
                "in-session handoff gate: OnAwake captures from the predecessor before replacing Instance");
            Assert.Contains("var predecessor = s_instance;", Collapse(body));
        }

        [Fact]
        public void EveryDropReasonHasAProducer()
        {
            // Producers: step A's pure decision (over every early kind, with and without a folder
            // change) and the explicit Drop sites in ParsekScenario.
            var produced = new HashSet<string>(StringComparer.Ordinal);
            foreach (EarlyLoadKind early in Enum.GetValues(typeof(EarlyLoadKind)))
            {
                string same = InSessionStagedStateHandoff.DecideStepA(early, "f", "f");
                string moved = InSessionStagedStateHandoff.DecideStepA(early, "f", "g");
                if (same != null) produced.Add(same);
                if (moved != null) produced.Add(moved);
            }

            string scenario = SourceScanText.StripCommentsAndMaskLiterals(ReadParsekSource(ScenarioPath))
                + SourceScanText.StripCommentsAndMaskLiterals(ReadParsekSource(HandoffPartialPath));
            var dropSite = new Regex(@"InSessionStagedStateHandoff\.Drop\(\s*InSessionStagedStateHandoff\.(\w+)\s*\)");
            var sites = dropSite.Matches(scenario).Cast<Match>().Select(m => m.Groups[1].Value).ToList();
            Assert.Equal(new[] { "DropInertGameMode", "DropMainMenu", "DropSaveFolderChanged", "DropStagedStateNotLoaded" },
                sites.OrderBy(s => s, StringComparer.Ordinal));
            foreach (string field in sites)
            {
                var fi = typeof(InSessionStagedStateHandoff).GetField(field,
                    System.Reflection.BindingFlags.Static | System.Reflection.BindingFlags.NonPublic);
                Assert.NotNull(fi);
                produced.Add((string)fi.GetValue(null));
            }

            Assert.Equal(
                InSessionStagedStateHandoff.DropReasons.OrderBy(s => s, StringComparer.Ordinal),
                produced.OrderBy(s => s, StringComparer.Ordinal));

            string mainMenu = PreparedMethodBody(ScenarioPath, "private void OnMainMenuTransition(GameScenes newScene)");
            Assert.Contains("InSessionStagedStateHandoff.Drop(InSessionStagedStateHandoff.DropMainMenu);", mainMenu);
            string folder = PreparedMethodBody(ScenarioPath, "private void DetectSaveFolderChange()");
            Assert.Contains("InSessionStagedStateHandoff.Drop(InSessionStagedStateHandoff.DropSaveFolderChanged);", folder);
            string capture = PreparedMethodBody(HandoffPartialPath, "internal void CaptureInSessionStagedStateHandoff(string reason)");
            Assert.Contains("InSessionStagedStateHandoff.Drop(InSessionStagedStateHandoff.DropStagedStateNotLoaded);", capture);
        }

        [Fact]
        public void TheHandoffHasOneConsumerAndOneCaptureWrapper()
        {
            var takeSites = new List<string>();
            var captureSites = new List<string>();
            var wrapperSites = new List<string>();
            foreach (var (rel, prepared) in ParsekSources())
            {
                for (int i = 0; i < Occurrences(prepared, "InSessionStagedStateHandoff.Take("); i++)
                    takeSites.Add(rel);
                for (int i = 0; i < Occurrences(prepared, "InSessionStagedStateHandoff.Capture("); i++)
                    captureSites.Add(rel);
                for (int i = 0; i < Occurrences(prepared, ".CaptureInSessionStagedStateHandoff(")
                         + Occurrences(prepared, " CaptureInSessionStagedStateHandoff("); i++)
                    wrapperSites.Add(rel);
            }
            Assert.Equal(new[] { HandoffPartialPath }, takeSites);
            Assert.Equal(new[] { HandoffPartialPath }, captureSites);
            // OnDestroy and OnAwake; the partial holds the declaration.
            Assert.Equal(new[] { ScenarioPath, ScenarioPath },
                wrapperSites.Where(s => s != HandoffPartialPath).ToArray());
        }

        [Fact]
        public void BothReFlyDiscardHelpersPruneTheRowsNamingTheAttempt()
        {
            string prune = PreparedMethodBody(DiscardPath,
                "internal static AttemptDiscardSummary PruneActiveReFlyAttemptOwnedTopology(");
            Assert.Equal(2, Occurrences(prune, "PruneStagedRowsNamingAttempt("));
            int collect = IndexOrFail(prune, "summary.AttemptIds = CollectReFlyAttemptOwnedRecordingIds(");
            int rows = prune.LastIndexOf("PruneStagedRowsNamingAttempt(", StringComparison.Ordinal);
            Assert.True(collect < rows);

            string discard = PreparedMethodBody(DiscardPath,
                "private static ReFlyAttemptDiscardResult DiscardReFlyAttemptRecordingsAndRewindPoints(");
            Assert.Equal(1, Occurrences(discard, "PruneStagedRowsNamingAttempt("));
            Assert.Contains("PruneStagedRowsNamingAttempt(scenario, result.AttemptIds, reason);", Collapse(discard));
        }

        // ---- helpers ----

        private static IEnumerable<(string rel, string prepared)> ParsekSources()
        {
            string root = ParsekSourceRoot();
            foreach (string path in Directory.GetFiles(root, "*.cs", SearchOption.AllDirectories))
            {
                string rel = path.Substring(root.Length)
                    .TrimStart(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar)
                    .Replace('\\', '/');
                if (rel.StartsWith("bin/", StringComparison.OrdinalIgnoreCase)
                    || rel.StartsWith("obj/", StringComparison.OrdinalIgnoreCase))
                    continue;
                yield return (rel, SourceScanText.StripCommentsAndMaskLiterals(File.ReadAllText(path)));
            }
        }

        private static string PreparedMethodBody(string relPath, string signatureFragment)
        {
            string prepared = SourceScanText.StripCommentsAndMaskLiterals(ReadParsekSource(relPath));
            int sigIdx = prepared.IndexOf(signatureFragment, StringComparison.Ordinal);
            Assert.True(sigIdx >= 0, "in-session handoff gate: signature not found in " + relPath + ": " + signatureFragment);
            Assert.True(prepared.IndexOf(signatureFragment, sigIdx + 1, StringComparison.Ordinal) < 0,
                "in-session handoff gate: signature is ambiguous in " + relPath + ": " + signatureFragment);
            int open = prepared.IndexOf('{', sigIdx);
            return SourceScanText.BraceMatchedBlock(prepared, open);
        }

        private static int IndexOrFail(string body, string needle)
        {
            int idx = body.IndexOf(needle, StringComparison.Ordinal);
            Assert.True(idx >= 0, "in-session handoff gate: '" + needle + "' not found");
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

        private static string Collapse(string text) => Regex.Replace(text, @"\s+", " ");

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
