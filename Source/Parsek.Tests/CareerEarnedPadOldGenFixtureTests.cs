using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using Xunit;

namespace Parsek.Tests
{
    /// <summary>
    /// Drives the committed harness fixture <c>career-earned-pad-oldgen</c> (the host of
    /// <c>GC-3-other-generation-tree-kept-inert</c>) through the product load / reconcile /
    /// save path headlessly, so the log literals that lane pins are emitted by the real code
    /// on the real fixture bytes rather than only matched by hand against the source. The
    /// fixture is career-earned-pad with the tree-level generation 4 -> 3 and the ledger header
    /// generation 4 -> 3 (harness/tools/build_career_earned_pad_oldgen.py).
    /// </summary>
    [Collection("Sequential")]
    public class CareerEarnedPadOldGenFixtureTests : IDisposable
    {
        private const string TreeId = "5eb172ab29034ccbbac497ce6406c149";
        private const double SaveClockUT = 408.72;

        private readonly List<string> logLines = new List<string>();
        private readonly GameScenes originalScene;
        private readonly string tempDir;

        public CareerEarnedPadOldGenFixtureTests()
        {
            originalScene = HighLogic.LoadedScene;
            HighLogic.LoadedScene = GameScenes.FLIGHT;
            RecordingStore.SuppressLogging = true;
            GameStateStore.SuppressLogging = true;
            RecordingStore.ResetForTesting();
            Ledger.ResetForTesting();
            MissionStore.ResetForTesting();
            GroupHierarchyStore.ResetForTesting();
            RecalculationEngine.ClearModules();
            ParsekScenario.ResetInstanceForTesting();
            EffectiveState.ResetCachesForTesting();
            ParsekLog.ResetTestOverrides();
            ParsekLog.SuppressLogging = false;
            ParsekLog.TestSinkForTesting = line => logLines.Add(line);
            tempDir = Path.Combine(Path.GetTempPath(), "parsek-gc3-oldgen-" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(tempDir);
        }

        public void Dispose()
        {
            ParsekLog.ResetTestOverrides();
            ParsekLog.SuppressLogging = true;
            RecordingStore.ResetForTesting();
            RecordingStore.SuppressLogging = true;
            Ledger.ResetForTesting();
            MissionStore.ResetForTesting();
            GroupHierarchyStore.ResetForTesting();
            RecalculationEngine.ClearModules();
            ParsekScenario.ResetInstanceForTesting();
            EffectiveState.ResetCachesForTesting();
            HighLogic.LoadedScene = originalScene;
            try { Directory.Delete(tempDir, true); } catch (IOException) { } catch (UnauthorizedAccessException) { }
        }

        private static string FixtureDir()
        {
            return Path.GetFullPath(Path.Combine(AppDomain.CurrentDomain.BaseDirectory,
                "..", "..", "..", "..", "..", "harness", "fixtures", "saves", "career-earned-pad-oldgen"));
        }

        private static ConfigNode LoadParsekScenario()
        {
            ConfigNode root = ConfigNode.Load(Path.Combine(FixtureDir(), "persistent.sfs"));
            Assert.NotNull(root);
            ConfigNode game = root.GetNode("GAME") ?? root;
            ConfigNode scenario = game.GetNodes("SCENARIO")
                .FirstOrDefault(s => s.GetValue("name") == "ParsekScenario");
            Assert.NotNull(scenario);
            return scenario;
        }

        private string CopyLedgerToTemp()
        {
            // A copy, so the load's stale-temp sweep never runs inside the committed fixture.
            string dest = Path.Combine(tempDir, "ledger.pgld");
            File.Copy(Path.Combine(FixtureDir(), "Parsek", "GameState", "ledger.pgld"), dest);
            return dest;
        }

        private static string TreeNodeText(ConfigNode scenario)
        {
            return scenario.GetNodes("RECORDING_TREE").Single(t => t.GetValue("id") == TreeId).ToString();
        }

        // catches: the fixture no longer drives the inert path (a base re-harvest, a builder
        // edit, or a product change to the gate), or a pinned GC-3 lane literal drifting from
        // what the product writes for this tree.
        [Fact]
        public void Load_SetsTheTreeAsideInert_WithTheLanesLiterals()
        {
            ConfigNode scenario = LoadParsekScenario();
            ParsekScenario.LoadRecordingTrees(scenario, RecordingStore.CommittedRecordings);

            Assert.Empty(RecordingStore.CommittedTrees);
            Assert.Empty(RecordingStore.CommittedRecordings);
            Assert.Equal(1, InertRecordingTrees.Count);
            Assert.Contains(logLines, l => l.Contains("[RecordingTree]")
                && l.Contains("Load: skipped tree=" + TreeId
                    + " due to unsupported schema generation generation=3 expected=4"));
            Assert.Contains(logLines, l => l.Contains("[Scenario]")
                && l.Contains("LoadRecordingTrees: tree 'Jumping Flea' id=" + TreeId
                    + " was rejected by the schema gate (reason=tree-generation-older); kept inert, not loaded"));
            Assert.Contains(logLines, l => l.Contains("[InertTrees]")
                && l.Contains("1 committed tree(s) from a different recording schema generation kept inert "
                    + "(current generation=4, recordings=2): " + TreeId
                    + "(generation=3 reason=tree-generation-older). Their nodes are written back verbatim "
                    + "and their recording ids count as known; nothing plays or changes them"));
            Assert.DoesNotContain(logLines, l => l.Contains("LoadRecordingTrees: skipped tree '"));

            Assert.NotNull(InertRecordingTrees.TakeLoadNotice());
            Assert.Contains(logLines, l => l.Contains("[InertTrees]")
                && l.Contains("Load notice queued: 1 inert tree(s)"));
        }

        // catches: the save dropping the tree (the original bug) or rewriting any byte of it,
        // and the cold reload of that save not setting it aside again with the same counts.
        [Fact]
        public void Save_ReEmitsTheNodeVerbatim_AndTheReloadIsTheSame()
        {
            ConfigNode scenario = LoadParsekScenario();
            string original = TreeNodeText(scenario);
            ParsekScenario.LoadRecordingTrees(scenario, RecordingStore.CommittedRecordings);

            var saved = new ConfigNode("SCENARIO");
            ParsekScenario.SaveTreeRecordings(saved);
            Assert.Equal(original, TreeNodeText(saved));
            Assert.Contains(logLines, l => l.Contains("[InertTrees]")
                && l.Contains("OnSave: re-emitted 1 inert RECORDING_TREE node(s) verbatim (skipped=0)"));

            InertRecordingTrees.ResetForLoad("save-scoped-reset");
            logLines.Clear();
            ParsekScenario.LoadRecordingTrees(saved, RecordingStore.CommittedRecordings);
            Assert.Equal(1, InertRecordingTrees.Count);
            Assert.True(InertRecordingTrees.ContainsRecordingId("1d611e7533a64508ae6f3b305a51615e"));
            Assert.True(InertRecordingTrees.ContainsRecordingId("5436a7e8840b4c5885afcbaedc9dc037"));
            Assert.Contains(logLines, l => l.Contains("[InertTrees]")
                && l.Contains("kept inert (current generation=4, recordings=2): " + TreeId
                    + "(generation=3 reason=tree-generation-older)."));
        }

        // catches: the generation-3 ledger refused whole, or the reconcile pruning the 11 rows
        // tagged to the inert recordings (the lane's `before=16, kept=16 ... validRecordingIds=2`),
        // or the recalc no longer crediting the five milestones (the lane's `moduleCredited=5`).
        [Fact]
        public void Ledger_LoadsOnItsVersion_AndTheReconcileKeepsEveryRow()
        {
            ParsekScenario.LoadRecordingTrees(LoadParsekScenario(), RecordingStore.CommittedRecordings);

            Assert.True(Ledger.LoadFromFile(CopyLedgerToTemp()));
            Assert.Contains(logLines, l => l.Contains("[Ledger]")
                && l.Contains("ledger.pgld' carries recording schema generation '3' (current=4); "
                    + "ledger version matches, loaded anyway"));
            Assert.Equal(16, Ledger.Actions.Count);

            HashSet<string> valid = ParsekScenario.BuildValidRecordingIdsForKspLoad();
            Assert.Equal(2, valid.Count);
            Ledger.Reconcile(valid, SaveClockUT, preserveFutureTimelineActions: true);
            Assert.Equal(16, Ledger.Actions.Count);
            Assert.Contains(logLines, l => l.Contains("[Ledger]")
                && l.Contains("Reconcile complete: before=16, kept=16, prunedEarnings=0, prunedSpendings=0, "
                    + "prunedSpendingsByRecordingId=0, prunedContractLifecycle=0, prunedOther=0, maxUT=")
                && l.EndsWith(", validRecordingIds=2", StringComparison.Ordinal));

            var milestones = new MilestonesModule();
            RecalculationEngine.RegisterModule(milestones, RecalculationEngine.ModuleTier.FirstTier);
            RecalculationEngine.Recalculate(new List<GameAction>(EffectiveState.ComputeELS()));
            Assert.Equal(5, milestones.GetCreditedCount());
        }

        // catches: the pre-fix shape still reachable - with the holder empty (no inert tree),
        // the same reconcile prunes the earned rows. Keeps the lane's forbidden prune pattern a
        // real discriminator on this fixture.
        [Fact]
        public void WithoutTheInertHolder_TheReconcilePrunesTheTaggedRows()
        {
            Assert.True(Ledger.LoadFromFile(CopyLedgerToTemp()));
            Ledger.Reconcile(new HashSet<string>(StringComparer.Ordinal), SaveClockUT,
                preserveFutureTimelineActions: true);
            Assert.Equal(5, Ledger.Actions.Count);
            Assert.Contains(logLines, l => l.Contains("Reconcile complete: before=16, kept=5, prunedEarnings=8, ")
                && l.Contains("prunedOther=3,"));
        }
    }
}
