using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using Parsek;
using Parsek.Tests.Generators;
using Xunit;

namespace Parsek.Tests.Analyzer.Rules
{
    // INV7b annotation staleness. Loader-scoped: writes real .prec sidecars via
    // ScenarioWriter, then writes .pann sidecars via PannotationsSidecarBinary at a
    // chosen source epoch and runs the rule over the loaded model. Sequential
    // because the loader / probes touch RecordingStore statics. Each test names the
    // regression it guards.
    [Collection("Sequential")]
    public class Inv7bAnnotationStaleTests : IDisposable
    {
        private readonly string tempDir;
        private readonly bool prevSuppress;

        public Inv7bAnnotationStaleTests()
        {
            prevSuppress = RecordingStore.SuppressLogging;
            RecordingStore.SuppressLogging = false;
            RecordingStore.ResetForTesting();
            ParsekLog.SuppressLogging = true;

            tempDir = Path.Combine(Path.GetTempPath(),
                "parsek-analyzer-inv7b-" + Guid.NewGuid().ToString("N"));
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

        private string BuildSaveWithPrec(string id)
        {
            string saveDir = Path.Combine(tempDir, id);
            Directory.CreateDirectory(saveDir);
            var writer = new ScenarioWriter().WithV3Format().AddRecordingAsTree(
                new RecordingBuilder("Ann Craft").WithRecordingId(id)
                    .AddPoint(100, 0, 0, 1000).AddPoint(110, 0.01, 0.02, 1500));
            WriteSave(saveDir, writer);
            writer.WriteSidecarFiles(saveDir);
            return saveDir;
        }

        private static int PrecEpoch(string saveDir, string id)
        {
            string precPath = Path.Combine(saveDir, RecordingPaths.BuildTrajectoryRelativePath(id));
            Assert.True(TrajectorySidecarBinary.TryProbe(precPath, out TrajectorySidecarProbe probe));
            return probe.SidecarEpoch;
        }

        private static void WritePann(string saveDir, string id, int sourceEpoch)
        {
            string pannPath = Path.Combine(saveDir, RecordingPaths.BuildAnnotationsRelativePath(id));
            PannotationsSidecarBinary.Write(
                pannPath,
                id,
                sourceEpoch,
                RecordingStore.CurrentRecordingFormatVersion,
                new byte[32],
                new List<KeyValuePair<int, Parsek.Rendering.SmoothingSpline>>());
        }

        private static List<Finding> Run(string saveDir)
        {
            AnalyzerModel model = SaveDirectoryLoader.Load(saveDir, name => null);
            return new Inv7bAnnotationStale().Evaluate(model).ToList();
        }

        // Guards: a .pann whose SourceSidecarEpoch matches the paired .prec -> zero
        // INV7b findings. Fails if fresh annotations are flagged stale.
        [Fact]
        public void FreshAnnotation_NoFindings()
        {
            string saveDir = BuildSaveWithPrec("fresh0");
            WritePann(saveDir, "fresh0", PrecEpoch(saveDir, "fresh0"));

            Assert.Empty(Run(saveDir));
        }

        // Guards (edge case 15): a .pann whose SourceSidecarEpoch is behind the
        // paired .prec's current epoch -> WARN. Fails if a stale smoothing cache
        // passes, rendering a ghost from outdated annotations.
        [Fact]
        public void StaleAnnotation_Warns()
        {
            string saveDir = BuildSaveWithPrec("stale0");
            WritePann(saveDir, "stale0", PrecEpoch(saveDir, "stale0") - 1);

            List<Finding> findings = Run(saveDir);

            Finding warn = Assert.Single(findings);
            Assert.Equal(Inv7bAnnotationStale.RuleIdConst, warn.RuleId);
            Assert.Equal(VerdictLevel.Warn, warn.Level);
            Assert.Contains("error=stale", warn.Message);
        }

        // Guards (edge case 15): a .pann with no paired .prec is an orphan cache ->
        // WARN. Fails if orphan annotations pass silently.
        [Fact]
        public void OrphanAnnotation_Warns()
        {
            string saveDir = Path.Combine(tempDir, "orphan");
            Directory.CreateDirectory(saveDir);
            Directory.CreateDirectory(Path.Combine(saveDir, "Parsek", "Recordings"));
            File.WriteAllText(Path.Combine(saveDir, "persistent.sfs"),
                "GAME\n{\n\tFLIGHTSTATE\n\t{\n\t\tversion = 1.12.5\n\t}\n}\n");
            WritePann(saveDir, "orphan0", 3);

            List<Finding> findings = Run(saveDir);

            Finding warn = Assert.Single(findings);
            Assert.Equal(VerdictLevel.Warn, warn.Level);
            Assert.Contains("error=orphan", warn.Message);
        }

        // Guards (core-purity): a null SaveDirectory (in-memory model) -> zero
        // findings, no file access. Fails if INV7b reaches for files without a save
        // dir, blocking the H5 in-game reuse.
        [Fact]
        public void NullSaveDirectory_NoFindings()
        {
            var model = new AnalyzerModel { SaveName = "mem" };
            Assert.Empty(new Inv7bAnnotationStale().Evaluate(model).ToList());
        }
    }
}
