using System;
using System.IO;
using System.Linq;
using Parsek.Tests.Generators;
using Xunit;

namespace Parsek.Tests.Analyzer
{
    // Ledger + career parse tests (task 1.3). The ledger is loaded RAW (correction
    // C1): directly from ledger.pgld, never through Ledger.LoadFromFile, so no
    // process-static contamination. Sequential because Load toggles the
    // RecordingStore.SuppressLogging static.
    [Collection("Sequential")]
    public class SaveDirectoryLoaderLedgerTests : IDisposable
    {
        private readonly string tempDir;
        private readonly bool prevSuppress;

        public SaveDirectoryLoaderLedgerTests()
        {
            prevSuppress = RecordingStore.SuppressLogging;
            RecordingStore.SuppressLogging = false;
            RecordingStore.ResetForTesting();
            ParsekLog.SuppressLogging = true;

            tempDir = Path.Combine(Path.GetTempPath(),
                "parsek-analyzer-ledger-" + Guid.NewGuid().ToString("N"));
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

        private static CelestialBody NullResolver(string name) => null;

        private static void WriteLedger(string saveDir, params string[] actionIds)
        {
            string dir = Path.Combine(saveDir, "Parsek", "GameState");
            Directory.CreateDirectory(dir);

            var root = new ConfigNode("LEDGER");
            root.AddValue("version", "0");
            root.AddValue("recordingSchemaGeneration",
                RecordingStore.CurrentRecordingSchemaGeneration.ToString(
                    System.Globalization.CultureInfo.InvariantCulture));
            double ut = 10;
            foreach (string id in actionIds)
            {
                ConfigNode n = root.AddNode("GAME_ACTION");
                n.AddValue("actionId", id);
                n.AddValue("type", "0");
                n.AddValue("ut", ut.ToString(System.Globalization.CultureInfo.InvariantCulture));
                ut += 10;
            }
            root.Save(Path.Combine(dir, "ledger.pgld"));
        }

        // Guards (C1): every GAME_ACTION loads RAW and verbatim, with no ELS
        // pre-filtering. Fails if the loader dropped or reordered actions, which
        // would make INV8's dangling-tombstone check vacuous.
        [Fact]
        public void LedgerFile_LoadsRawVerbatim()
        {
            string saveDir = NewSaveDir("ledger");
            WriteLedger(saveDir, "act_1", "act_2", "act_3");

            AnalyzerModel model = SaveDirectoryLoader.Load(saveDir, NullResolver);

            Assert.Equal(3, model.Ledger.Count);
            var ids = model.Ledger.Select(a => a.ActionId).ToList();
            Assert.Equal(new[] { "act_1", "act_2", "act_3" }, ids);
            Assert.DoesNotContain(model.LoadFaults, f => f.FileKind == "ledger");
        }

        // Guards (module M-B2 loader gating change): a non-funds (Sandbox / Science)
        // save now flows a POPULATED CareerSave snapshot (Parsed == true) with every
        // per-facet HasX flag false, instead of the former null. The loader dropped the
        // HasFunds gate so the analyzer's careerSave export block is populated on a
        // career-but-non-funds save (the ledger-oracle verifier reads facet-absence
        // from the hasX flags, never from a missing block). No career or ledger fault.
        // Fails if the loader reinstates the HasFunds null-gate (which would alias
        // facet-absence with the tooling-absence "block missing" signal) or faults on
        // the absent funds facet.
        [Fact]
        public void NonFundsSave_ParsedSnapshot_FacetFlagsFalse_NoFault()
        {
            string saveDir = NewSaveDir("noncareer");
            var writer = new ScenarioWriter().AddRecordingAsTree(
                new RecordingBuilder("Sandbox Craft").WithRecordingId("sand0").AddPoint(100, 0, 0, 1000));
            string scenarioText = writer.SerializeConfigNode(writer.BuildScenarioNode(), "SCENARIO", 1);
            File.WriteAllText(Path.Combine(saveDir, "persistent.sfs"),
                "GAME\n{\n\tFLIGHTSTATE\n\t{\n\t\tversion = 1.12.5\n\t}\n" + scenarioText + "}\n");

            AnalyzerModel model = SaveDirectoryLoader.Load(saveDir, NullResolver);

            Assert.NotNull(model.CareerSave);
            Assert.True(model.CareerSave.Parsed);
            Assert.False(model.CareerSave.HasFunds);
            Assert.False(model.CareerSave.HasScience);
            Assert.False(model.CareerSave.HasRep);
            Assert.DoesNotContain(model.LoadFaults, f => f.FileKind == "career");
            Assert.DoesNotContain(model.LoadFaults, f => f.FileKind == "ledger");
            Assert.Empty(model.Ledger);
        }

        // Guards: a save carrying a Funding SCENARIO is recognized as career and its
        // parsed totals are surfaced on CareerSave (the HasFunds discriminator).
        [Fact]
        public void CareerSave_WhenFundingScenarioPresent_IsParsed()
        {
            string saveDir = NewSaveDir("career");
            File.WriteAllText(Path.Combine(saveDir, "persistent.sfs"),
                "GAME\n{\n" +
                "\tSCENARIO\n\t{\n\t\tname = Funding\n\t\tfunds = 12345.5\n\t}\n" +
                "\tFLIGHTSTATE\n\t{\n\t\tversion = 1.12.5\n\t}\n" +
                "}\n");

            AnalyzerModel model = SaveDirectoryLoader.Load(saveDir, NullResolver);

            Assert.NotNull(model.CareerSave);
            Assert.True(model.CareerSave.HasFunds);
            Assert.Equal(12345.5, model.CareerSave.Funds);
        }
    }
}
