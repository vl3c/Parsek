using System;
using System.Collections.Generic;
using System.IO;
using Parsek.Tests.Generators;
using Xunit;

namespace Parsek.Tests.Analyzer
{
    // Loader-scoped tests (design "Malformed-input robustness tests"). These touch
    // the RecordingStore.SuppressLogging static during load, so the class is
    // Sequential and restores the static in Dispose.
    [Collection("Sequential")]
    public class SaveDirectoryLoaderTests : IDisposable
    {
        private readonly string tempDir;
        private readonly bool prevSuppress;

        public SaveDirectoryLoaderTests()
        {
            prevSuppress = RecordingStore.SuppressLogging;
            RecordingStore.SuppressLogging = false;
            RecordingStore.ResetForTesting();
            ParsekLog.SuppressLogging = true;

            tempDir = Path.Combine(Path.GetTempPath(),
                "parsek-analyzer-loader-" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(tempDir);
        }

        public void Dispose()
        {
            RecordingStore.ResetForTesting();
            RecordingStore.SuppressLogging = prevSuppress;
            ParsekLog.SuppressLogging = true;

            if (Directory.Exists(tempDir))
            {
                try { Directory.Delete(tempDir, true); }
                catch { }
            }
        }

        private string NewSaveDir(string name)
        {
            string dir = Path.Combine(tempDir, name);
            Directory.CreateDirectory(dir);
            return dir;
        }

        // Writes a persistent.sfs wrapping the writer's SCENARIO (optionally mutated
        // to add supersede/tombstone ENTRY nodes ScenarioWriter cannot emit itself).
        private static void WriteSave(string saveDir, ScenarioWriter writer, Action<ConfigNode> mutateScenario = null)
        {
            ConfigNode scenario = writer.BuildScenarioNode();
            mutateScenario?.Invoke(scenario);
            string scenarioText = writer.SerializeConfigNode(scenario, "SCENARIO", 1);
            string save =
                "GAME\n{\n" +
                "\tFLIGHTSTATE\n\t{\n\t\tversion = 1.12.5\n\t}\n" +
                scenarioText +
                "}\n";
            File.WriteAllText(Path.Combine(saveDir, "persistent.sfs"), save);
        }

        private static CelestialBody NullResolver(string name) => null;

        // Guards: a synthetic ParsekScenario loads its trees + recordings +
        // supersede rows + tombstones with the expected counts. Fails if the loader
        // drops trees, miscounts recordings, or ignores the staging lists.
        [Fact]
        public void SyntheticSave_LoadsExpectedCounts()
        {
            string saveDir = NewSaveDir("synthetic");

            var segs = new[]
            {
                new RecordingBuilder("Flea Chain").WithRecordingId("seg0")
                    .AddPoint(100, 0, 0, 1000).WithChainId("c").WithChainIndex(0),
                new RecordingBuilder("Flea Chain").WithRecordingId("seg1")
                    .AddPoint(110, 0, 0, 1100).WithChainId("c").WithChainIndex(1)
                    .WithParentRecordingId("seg0"),
                new RecordingBuilder("Flea Chain").WithRecordingId("seg2")
                    .AddPoint(120, 0, 0, 1200).WithChainId("c").WithChainIndex(2)
                    .WithParentRecordingId("seg1"),
            };

            var writer = new ScenarioWriter().AddRecordingsAsTree(segs);

            WriteSave(saveDir, writer, scenario =>
            {
                var sup = scenario.AddNode("RECORDING_SUPERSEDES");
                new RecordingSupersedeRelation
                {
                    RelationId = "rsr_1",
                    OldRecordingId = "seg1",
                    NewRecordingId = "seg2",
                    UT = 120,
                }.SaveInto(sup);
                new RecordingSupersedeRelation
                {
                    RelationId = "rsr_2",
                    OldRecordingId = "seg0",
                    NewRecordingId = "seg1",
                    UT = 110,
                }.SaveInto(sup);

                var tomb = scenario.AddNode("LEDGER_TOMBSTONES");
                new LedgerTombstone
                {
                    TombstoneId = "tomb_1",
                    ActionId = "act_x",
                    RetiringRecordingId = "seg2",
                    UT = 120,
                }.SaveInto(tomb);
            });

            AnalyzerModel model = SaveDirectoryLoader.Load(saveDir, NullResolver);

            Assert.Empty(model.LoadFaults);
            Assert.Single(model.Trees);
            Assert.Equal(3, model.Recordings.Count);
            Assert.Equal(2, model.SupersedeRelations.Count);
            Assert.Single(model.Tombstones);

            Assert.Equal("seg0", model.Trees[0].RootRecordingId);
            var ids = new HashSet<string>();
            foreach (Recording r in model.Recordings)
                ids.Add(r.RecordingId);
            Assert.Contains("seg0", ids);
            Assert.Contains("seg1", ids);
            Assert.Contains("seg2", ids);

            Assert.Equal("act_x", model.Tombstones[0].ActionId);
            Assert.Equal("synthetic", model.SaveName);
        }

        // Guards: a corrupt persistent.sfs yields exactly one sfs LoadFault and an
        // empty model - triage must not crash on a malformed save.
        [Fact]
        public void CorruptSfs_ProducesSingleSfsFault_EmptyModel()
        {
            string saveDir = NewSaveDir("corrupt");
            File.WriteAllText(Path.Combine(saveDir, "persistent.sfs"),
                "GAME\n{\n\tSCENARIO\n\t{\n\t\tname = ParsekScenario\n{{{ broken unbalanced");

            AnalyzerModel model = SaveDirectoryLoader.Load(saveDir, NullResolver);

            Assert.Single(model.LoadFaults);
            Assert.Equal("sfs", model.LoadFaults[0].FileKind);
            Assert.Empty(model.Recordings);
            Assert.Empty(model.Trees);
            Assert.Empty(model.Tombstones);
            Assert.Empty(model.SupersedeRelations);

            // Blocker regression: the sfs fault must reach the report as a red run,
            // not analyze GREEN. Route through the full Evaluate pipeline (not just
            // the model) so the LOADER-FAULT rule + verdict policy are exercised.
            AnalysisReport report = InvariantEvaluator.Evaluate(model);
            Assert.True(report.IsRed);
            Assert.Contains(report.Findings, f =>
                f.RuleId == Parsek.Analyzer.Rules.LoadFaultRule.RuleIdConst
                && f.Level == VerdictLevel.Fail
                && f.Message.Contains("kind=sfs"));
        }

        // Guards: a save with no Parsek footprint (a non-Parsek SCENARIO, or no sfs
        // at all) loads cleanly with zero faults and an empty model.
        [Fact]
        public void NonParsekSave_NoFaults_EmptyModel()
        {
            string saveDir = NewSaveDir("nonparsek");
            File.WriteAllText(Path.Combine(saveDir, "persistent.sfs"),
                "GAME\n{\n" +
                "\tSCENARIO\n\t{\n\t\tname = SomeOther\n\t\tscene = 5\n\t}\n" +
                "\tFLIGHTSTATE\n\t{\n\t\tversion = 1.12.5\n\t}\n" +
                "}\n");

            AnalyzerModel model = SaveDirectoryLoader.Load(saveDir, NullResolver);

            Assert.Empty(model.LoadFaults);
            Assert.Empty(model.Recordings);
            Assert.Empty(model.Trees);
            Assert.Empty(model.Tombstones);
        }

        [Fact]
        public void MissingSfs_NoFaults_EmptyModel()
        {
            string saveDir = NewSaveDir("empty");

            AnalyzerModel model = SaveDirectoryLoader.Load(saveDir, NullResolver);

            Assert.Empty(model.LoadFaults);
            Assert.Empty(model.Recordings);
            Assert.Empty(model.Trees);
        }

        // Guards: the loader parses a fixture-generation.txt stamp into
        // model.FixtureStamp (order/whitespace tolerant); a save with no stamp file
        // yields a null FixtureStamp (non-fixture subject -> STALE check skipped).
        [Fact]
        public void FixtureStamp_ParsedWhenPresent_NullWhenAbsent()
        {
            string stampedDir = NewSaveDir("stamped");
            File.WriteAllText(Path.Combine(stampedDir, "persistent.sfs"),
                "GAME\n{\n\tFLIGHTSTATE\n\t{\n\t\tversion = 1.12.5\n\t}\n}\n");
            File.WriteAllText(Path.Combine(stampedDir, "fixture-generation.txt"),
                "generation=3 provenance=harvested\n");

            AnalyzerModel stamped = SaveDirectoryLoader.Load(stampedDir, NullResolver);
            Assert.NotNull(stamped.FixtureStamp);
            Assert.Equal(3, stamped.FixtureStamp.Value.SchemaGeneration);
            Assert.Equal("harvested", stamped.FixtureStamp.Value.Provenance);

            string plainDir = NewSaveDir("unstamped");
            File.WriteAllText(Path.Combine(plainDir, "persistent.sfs"),
                "GAME\n{\n\tFLIGHTSTATE\n\t{\n\t\tversion = 1.12.5\n\t}\n}\n");
            AnalyzerModel plain = SaveDirectoryLoader.Load(plainDir, NullResolver);
            Assert.Null(plain.FixtureStamp);
        }

        // Guards: the loader restores RecordingStore.SuppressLogging to its prior
        // value even on the happy path, so it does not leak analyzer state into a
        // subsequent test or a live session.
        [Fact]
        public void Load_RestoresSuppressLogging()
        {
            string saveDir = NewSaveDir("suppress");
            var writer = new ScenarioWriter().AddRecordingAsTree(
                new RecordingBuilder("Solo").WithRecordingId("solo0").AddPoint(100, 0, 0, 1000));
            WriteSave(saveDir, writer);

            RecordingStore.SuppressLogging = false;
            SaveDirectoryLoader.Load(saveDir, NullResolver);
            Assert.False(RecordingStore.SuppressLogging);

            RecordingStore.SuppressLogging = true;
            SaveDirectoryLoader.Load(saveDir, NullResolver);
            Assert.True(RecordingStore.SuppressLogging);
        }
    }
}
