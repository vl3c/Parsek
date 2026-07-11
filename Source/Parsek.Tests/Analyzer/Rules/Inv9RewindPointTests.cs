using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using Parsek;
using Parsek.Tests.Generators;
using Xunit;

namespace Parsek.Tests.Analyzer.Rules
{
    // INV9 rewind-point + id validation. Loader-scoped fixtures write real
    // RewindPoints/<id>.sfs quicksaves; the traversal-id case is pure in-memory to
    // prove validation fires without a filesystem touch. Sequential because the
    // loader touches RecordingStore statics. Each test names the regression.
    [Collection("Sequential")]
    public class Inv9RewindPointTests : IDisposable
    {
        private readonly string tempDir;
        private readonly bool prevSuppress;

        public Inv9RewindPointTests()
        {
            prevSuppress = RecordingStore.SuppressLogging;
            RecordingStore.SuppressLogging = false;
            RecordingStore.ResetForTesting();
            ParsekLog.SuppressLogging = true;

            tempDir = Path.Combine(Path.GetTempPath(),
                "parsek-analyzer-inv9-" + Guid.NewGuid().ToString("N"));
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

        private static void WriteSave(string saveDir, ScenarioWriter writer)
        {
            string scenarioText = writer.SerializeConfigNode(writer.BuildScenarioNode(), "SCENARIO", 1);
            string save =
                "GAME\n{\n\tFLIGHTSTATE\n\t{\n\t\tversion = 1.12.5\n\t}\n" + scenarioText + "}\n";
            File.WriteAllText(Path.Combine(saveDir, "persistent.sfs"), save);
        }

        private static void WriteRewindPoint(string saveDir, string rpId)
        {
            string dir = Path.Combine(saveDir, RecordingPaths.RewindPointsSubdir);
            Directory.CreateDirectory(dir);
            File.WriteAllText(Path.Combine(dir, rpId + ".sfs"),
                "GAME\n{\n\tversion = 1.12.5\n}\n");
        }

        private string BuildSave(string name, RecordingBuilder builder)
        {
            string saveDir = Path.Combine(tempDir, name);
            Directory.CreateDirectory(saveDir);
            var writer = new ScenarioWriter().WithV3Format().AddRecordingAsTree(builder);
            WriteSave(saveDir, writer);
            writer.WriteSidecarFiles(saveDir);
            return saveDir;
        }

        private static List<Finding> Run(string saveDir)
        {
            AnalyzerModel model = SaveDirectoryLoader.Load(saveDir, name => null);
            return new Inv9RewindPoint().Evaluate(model).ToList();
        }

        // Guards: a valid RewindPoint id whose Parsek/RewindPoints/<id>.sfs exists
        // and parses -> zero INV9 findings. Fails if a present, valid RP quicksave
        // is flagged missing.
        [Fact]
        public void ValidRewindPoint_Present_NoFindings()
        {
            string saveDir = BuildSave("valid",
                new RecordingBuilder("RP Craft").WithRecordingId("rp0")
                    .AddPoint(100, 0, 0, 1000).WithRewindSave("rpfile0"));
            WriteRewindPoint(saveDir, "rpfile0");

            Assert.Empty(Run(saveDir));
        }

        // Guards (edge case 19): a RewindPoint id with a path-traversal sequence ->
        // FAIL via ValidateRecordingId, emitted from a pure in-memory model whose
        // SaveDirectory is a nonexistent path, so the escaped path is never touched.
        // Fails if a traversal id reaches the filesystem (security regression).
        [Fact]
        public void TraversalRewindId_Fails_WithoutFsAccess()
        {
            var model = new AnalyzerModel
            {
                SaveName = "trav",
                SaveDirectory = Path.Combine(tempDir, "does-not-exist"),
                Recordings = new List<Recording>
                {
                    new Recording { RecordingId = "rec0", RewindSaveFileName = "../evil" },
                },
            };

            List<Finding> findings = new Inv9RewindPoint().Evaluate(model).ToList();

            Finding fail = Assert.Single(findings);
            Assert.Equal(VerdictLevel.Fail, fail.Level);
            Assert.Contains("badid", fail.Message);
            Assert.Contains("../evil", fail.Message);
        }

        // Guards: a recording referencing an absent RP file -> FAIL. Fails if a
        // dangling RewindPoint reference passes (re-fly would fail at load).
        [Fact]
        public void MissingRewindPoint_Fails()
        {
            string saveDir = BuildSave("missing",
                new RecordingBuilder("RP Craft").WithRecordingId("rp1")
                    .AddPoint(100, 0, 0, 1000).WithRewindSave("rpfile-missing"));

            List<Finding> findings = Run(saveDir);

            Finding fail = Assert.Single(findings, f => f.Level == VerdictLevel.Fail);
            Assert.Contains("missing-rewindpoint", fail.Message);
        }

        // Guards: an RP quicksave on disk that no recording references -> WARN.
        // Fails if orphan rewind points are ignored.
        [Fact]
        public void OrphanRewindPoint_Warns()
        {
            string saveDir = BuildSave("orphan",
                new RecordingBuilder("RP Craft").WithRecordingId("rp2").AddPoint(100, 0, 0, 1000));
            WriteRewindPoint(saveDir, "stray-rp");

            List<Finding> findings = Run(saveDir);

            Finding warn = Assert.Single(findings, f => f.Level == VerdictLevel.Warn);
            Assert.Equal("stray-rp", warn.Target);
            Assert.Contains("orphan-rewindpoint", warn.Message);
        }
    }
}
