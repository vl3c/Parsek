using System;
using System.IO;
using Parsek;
using Parsek.Tests.Generators;
using Xunit;

namespace Parsek.Tests.Analyzer
{
    // The three-mode entry point (design "Run modes" + "Where the code lives").
    //
    // - CI regression floor: a non-Manual test synthesizes a fixture corpus with
    //   ScenarioWriter, runs Analyzer.Run over it, and asserts the run is GREEN
    //   (zero FAIL, zero STALE-FIXTURE) with both report files written. This is the
    //   per-PR floor that fails the build if any rule false-alarms on known-good
    //   builder output, or if a builder starts emitting invariant-violating data.
    // - Harness post-run / ad hoc triage: a [Trait("Category","Manual")] test reads
    //   PARSEK_ANALYZER_SAVE (and optional PARSEK_ANALYZER_RESULTS), runs the same
    //   pipeline, and writes the reports. It SKIPS CLEANLY when the env var is unset
    //   so it never runs (or fails) in the normal CI pass.
    //
    // Sequential because Analyzer.Run drives the loader, which touches
    // RecordingStore statics.
    [Collection("Sequential")]
    public class OfflineAnalyzerTests : IDisposable
    {
        private readonly string tempDir;
        private readonly bool prevSuppress;

        public OfflineAnalyzerTests()
        {
            prevSuppress = RecordingStore.SuppressLogging;
            RecordingStore.SuppressLogging = false;
            RecordingStore.ResetForTesting();
            ParsekLog.SuppressLogging = true;

            tempDir = Path.Combine(Path.GetTempPath(),
                "parsek-analyzer-offline-" + Guid.NewGuid().ToString("N"));
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

        private static CelestialBody Resolver(string name) => TestBodyRegistry.CreateBody(name);

        // Default results dir when PARSEK_ANALYZER_RESULTS is unset:
        // bin/Debug/net472/analyzer-results/ (AppContext.BaseDirectory IS that dir).
        private static string DefaultResultsDir() =>
            Path.Combine(AppContext.BaseDirectory, "analyzer-results");

        private void SynthesizeCorpus(string saveDir)
        {
            Directory.CreateDirectory(saveDir);
            var writer = new ScenarioWriter().WithV3Format()
                .AddRecordingAsTree(new RecordingBuilder("Corpus A")
                    .WithRecordingId("corpus0")
                    .AddPoint(100, 0, 0, 1000)
                    .AddPoint(110, 0.01, 0.02, 1500)
                    .WithVesselSnapshot(VesselSnapshotBuilder.FleaRocket("Corpus A", "Jeb", pid: 8001)))
                .AddRecordingAsTree(new RecordingBuilder("Corpus B")
                    .WithRecordingId("corpus1")
                    .AddPoint(200, 1, 1, 2000)
                    .AddPoint(210, 1.01, 1.02, 2500)
                    .WithVesselSnapshot(VesselSnapshotBuilder.ProbeShip("Corpus B", pid: 8002)));

            string scenarioText = writer.SerializeConfigNode(writer.BuildScenarioNode(), "SCENARIO", 1);
            string save =
                "GAME\n{\n\tFLIGHTSTATE\n\t{\n\t\tversion = 1.12.5\n\t}\n" + scenarioText + "}\n";
            File.WriteAllText(Path.Combine(saveDir, "persistent.sfs"), save);
            writer.WriteSidecarFiles(saveDir);
        }

        // Guards (CI regression floor): the full analyzer pipeline over a known-good
        // ScenarioWriter corpus is GREEN (zero FAIL, zero STALE) and writes both
        // report files. Fails if a rule false-alarms on valid builder output, if a
        // builder emits invariant-violating data, or if the report writer breaks.
        [Fact]
        public void CiFloor_SyntheticCorpus_IsGreen_AndWritesReports()
        {
            string saveDir = Path.Combine(tempDir, "corpus");
            SynthesizeCorpus(saveDir);
            string resultsDir = Path.Combine(tempDir, "results");

            AnalysisReport report = Analyzer.Run(saveDir, resultsDir, Resolver);

            Assert.Equal(0, report.Counts.Fail);
            Assert.Equal(0, report.Counts.StaleFixture);
            Assert.False(report.IsRed);

            Assert.True(File.Exists(Path.Combine(resultsDir, "corpus.analysis.json")));
            Assert.True(File.Exists(Path.Combine(resultsDir, "corpus.analysis.txt")));
        }

        // Harness post-run / ad hoc triage. Reads PARSEK_ANALYZER_SAVE; when unset
        // it skips cleanly (no-op) so the normal CI pass never runs it. When set, it
        // runs the pipeline over that save and writes the reports into
        // PARSEK_ANALYZER_RESULTS (or the default analyzer-results dir). Malformed
        // input is expected to produce findings, never a stack trace.
        [Fact]
        [Trait("Category", "Manual")]
        public void Manual_AnalyzeEnvSave_WritesReports()
        {
            string saveDir = Environment.GetEnvironmentVariable("PARSEK_ANALYZER_SAVE");
            if (string.IsNullOrEmpty(saveDir))
                return; // env unset -> skip cleanly (CI-safe)

            string resultsDir = Environment.GetEnvironmentVariable("PARSEK_ANALYZER_RESULTS");
            if (string.IsNullOrEmpty(resultsDir))
                resultsDir = DefaultResultsDir();

            AnalysisReport report = Analyzer.Run(saveDir, resultsDir, Resolver);

            string baseName = string.IsNullOrEmpty(report.SaveName) ? "analysis" : report.SaveName;
            Assert.True(File.Exists(Path.Combine(resultsDir, baseName + ".analysis.json")),
                "analyzer must write the machine report for a Manual run");
            Assert.True(File.Exists(Path.Combine(resultsDir, baseName + ".analysis.txt")),
                "analyzer must write the human report for a Manual run");
        }
    }
}
