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
    //   ScenarioWriter, runs OfflineAnalyzer.Run over it, and asserts the run is GREEN
    //   (zero FAIL, zero STALE-FIXTURE) with both report files written. This is the
    //   per-PR floor that fails the build if any rule false-alarms on known-good
    //   builder output, or if a builder starts emitting invariant-violating data.
    // - Harness post-run / ad hoc triage: a [Trait("Category","Manual")] test reads
    //   PARSEK_ANALYZER_SAVE (and optional PARSEK_ANALYZER_RESULTS), runs the same
    //   pipeline, and writes the reports. It SKIPS CLEANLY when the env var is unset
    //   so it never runs (or fails) in the normal CI pass.
    //
    // Sequential because OfflineAnalyzer.Run drives the loader, which touches
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

        // A corpus that REDS on a seeded INV2 overlap: two atmospheric TrackSections
        // with overlapping interior UT spans on one recording (double-cover -> FAIL).
        private void SynthesizeRedCorpus(string saveDir)
        {
            Directory.CreateDirectory(saveDir);
            var writer = new ScenarioWriter().WithV3Format()
                .AddRecordingAsTree(new RecordingBuilder("Red A")
                    .WithRecordingId("red0")
                    .AddPoint(100, 0, 0, 1000)
                    .AddPoint(115, 0.01, 0.02, 1500)
                    .AddAtmosphericSection(100, 110)
                    .AddAtmosphericSection(105, 115) // overlaps [105,110] -> INV2 FAIL
                    .WithVesselSnapshot(VesselSnapshotBuilder.FleaRocket("Red A", "Jeb", pid: 8101)));

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

            AnalysisReport report = OfflineAnalyzer.Run(saveDir, resultsDir, Resolver);

            Assert.Equal(0, report.Counts.Fail);
            Assert.Equal(0, report.Counts.StaleFixture);
            Assert.False(report.IsRed);

            Assert.True(File.Exists(Path.Combine(resultsDir, "corpus.analysis.json")));
            Assert.True(File.Exists(Path.Combine(resultsDir, "corpus.analysis.txt")));
        }

        // Guards: PARSEK_ANALYZER_BASELINE_MODE parse contract (case-insensitive;
        // default ignore on unset / unrecognized).
        // (expected passed as the enum NAME so the public test signature does not
        // expose the internal BaselineMode type.)
        [Theory]
        [InlineData(null, "Ignore")]
        [InlineData("", "Ignore")]
        [InlineData("ignore", "Ignore")]
        [InlineData("APPLY", "Apply")]
        [InlineData(" Apply ", "Apply")]
        [InlineData("Forbid", "Forbid")]
        [InlineData("nonsense", "Ignore")]
        public void ParseBaselineMode_MapsEnvValues(string value, string expectedName)
        {
            Assert.Equal(expectedName, OfflineAnalyzer.ParseBaselineMode(value).ToString());
        }

        // Guards (design "Write then use is green"): -WriteBaseline over a corpus that
        // reds on a seeded INV2 overlap, then a subsequent Apply run over the same
        // save, exits green. Fails if authored baselines do not actually green a
        // subsequent gated run (the core user story).
        [Fact]
        public void WriteThenApply_RedCorpus_GoesGreen_FindingsRemain()
        {
            string saveDir = Path.Combine(tempDir, "redsave");
            SynthesizeRedCorpus(saveDir);
            string resultsDir = Path.Combine(tempDir, "results");

            // No baseline yet: the seeded overlap reds under Ignore.
            AnalysisReport before = OfflineAnalyzer.Run(saveDir, resultsDir, Resolver, BaselineMode.Ignore);
            Assert.True(before.IsRed);
            Assert.Contains(before.Findings, f => f.RuleId == "INV2-NO-DOUBLE-COVER" && f.Level == VerdictLevel.Fail);

            // Author the baseline, then re-run gated in Apply.
            OfflineAnalyzer.WriteBaselineForSave(saveDir, resultsDir, Resolver, keepStale: false);
            Assert.True(File.Exists(OfflineAnalyzer.ResolveBaselinePath(saveDir)));

            AnalysisReport after = OfflineAnalyzer.Run(saveDir, resultsDir, Resolver, BaselineMode.Apply);
            Assert.False(after.IsRed);
            Assert.True(after.Counts.Baselined > 0);
            // The finding is still in the report, just accepted.
            Assert.Contains(after.Findings, f => f.RuleId == "INV2-NO-DOUBLE-COVER" && f.Baselined);
        }

        // Guards: Forbid over a save that carries a baseline reds (BASELINE-FORBIDDEN),
        // even though the seeded reds were baselined. The fresh-save guard is
        // structural.
        [Fact]
        public void Forbid_OverBaselinedSave_Reds()
        {
            string saveDir = Path.Combine(tempDir, "redsave");
            SynthesizeRedCorpus(saveDir);
            string resultsDir = Path.Combine(tempDir, "results");
            OfflineAnalyzer.WriteBaselineForSave(saveDir, resultsDir, Resolver, keepStale: false);

            AnalysisReport report = OfflineAnalyzer.Run(saveDir, resultsDir, Resolver, BaselineMode.Forbid);
            Assert.True(report.IsRed);
            Assert.Contains(report.Findings, f => f.RuleId == BaselineFilter.ForbiddenRuleId);
        }

        // Guards: Forbid over a baseline-free GREEN corpus is a clean green no-op.
        [Fact]
        public void Forbid_NoBaseline_GreenCorpus_Clean()
        {
            string saveDir = Path.Combine(tempDir, "greensave");
            SynthesizeCorpus(saveDir);
            string resultsDir = Path.Combine(tempDir, "results");

            AnalysisReport report = OfflineAnalyzer.Run(saveDir, resultsDir, Resolver, BaselineMode.Forbid);
            Assert.False(report.IsRed);
            Assert.DoesNotContain(report.Findings, f => f.RuleId == BaselineFilter.ForbiddenRuleId);
        }

        // Guards: Ignore over a save WITH a baseline never applies it; the seeded reds
        // stay red and a PRESENT-NOT-APPLIED INFO is emitted (so WriteBaseline's
        // internal Ignore pass sees the TRUE findings).
        [Fact]
        public void Ignore_OverBaselinedSave_LeavesRedsRed()
        {
            string saveDir = Path.Combine(tempDir, "redsave");
            SynthesizeRedCorpus(saveDir);
            string resultsDir = Path.Combine(tempDir, "results");
            OfflineAnalyzer.WriteBaselineForSave(saveDir, resultsDir, Resolver, keepStale: false);

            AnalysisReport report = OfflineAnalyzer.Run(saveDir, resultsDir, Resolver, BaselineMode.Ignore);
            Assert.True(report.IsRed);
            Assert.Equal(0, report.Counts.Baselined);
            Assert.Contains(report.Findings, f => f.RuleId == BaselineFilter.PresentNotAppliedRuleId);
        }

        // Guards (SF4): -WriteBaseline over a save whose EXISTING baseline.cfg has a
        // HARD load fault (a hand-edit syntax error) REFUSES the write and leaves the
        // file byte-for-byte untouched, rather than silently rewriting it and
        // destroying every human-authored reason. Fails if a corrupt existing baseline
        // is silently clobbered.
        [Fact]
        public void WriteBaseline_ExistingBaselineHardFault_Refuses_FileUntouched()
        {
            string saveDir = Path.Combine(tempDir, "redsave");
            SynthesizeRedCorpus(saveDir);
            string resultsDir = Path.Combine(tempDir, "results");

            // Author a valid baseline first, then corrupt it (unbalanced braces = a
            // hard ParseFault) while preserving a human reason line.
            OfflineAnalyzer.WriteBaselineForSave(saveDir, resultsDir, Resolver, keepStale: false);
            string baselinePath = OfflineAnalyzer.ResolveBaselinePath(saveDir);
            File.WriteAllText(baselinePath,
                "baselineFormatVersion = 1\n"
                + "reason = HUMAN: do not lose me\n"
                + "ENTRY\n{\n  ruleId = INV2-NO-DOUBLE-COVER\n  target = red0\n"); // no closing brace
            byte[] before = File.ReadAllBytes(baselinePath);

            // The write is refused with the fault detail; the file is not rewritten.
            Assert.Throws<InvalidOperationException>(() =>
                OfflineAnalyzer.WriteBaselineForSave(saveDir, resultsDir, Resolver, keepStale: false));

            byte[] after = File.ReadAllBytes(baselinePath);
            Assert.Equal(before, after);
        }

        // Harness post-run / ad hoc triage. Reads PARSEK_ANALYZER_SAVE; when unset
        // it skips cleanly (no-op) so the normal CI pass never runs it. When set, it
        // runs the pipeline over that save (in the PARSEK_ANALYZER_BASELINE_MODE the
        // env resolves to; default ignore) and writes the reports into
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

            BaselineMode mode = OfflineAnalyzer.ParseBaselineMode(
                Environment.GetEnvironmentVariable("PARSEK_ANALYZER_BASELINE_MODE"));

            AnalysisReport report = OfflineAnalyzer.Run(saveDir, resultsDir, Resolver, mode);

            string baseName = string.IsNullOrEmpty(report.SaveName) ? "analysis" : report.SaveName;
            Assert.True(File.Exists(Path.Combine(resultsDir, baseName + ".analysis.json")),
                "analyzer must write the machine report for a Manual run");
            Assert.True(File.Exists(Path.Combine(resultsDir, baseName + ".analysis.txt")),
                "analyzer must write the human report for a Manual run");
        }

        // Authoring path for -WriteBaseline (design "Authoring: -WriteBaseline").
        // Reads PARSEK_ANALYZER_SAVE; skips cleanly when unset. Refuses to run when
        // PARSEK_ANALYZER_BASELINE_MODE=forbid is declared in the environment (a
        // caller explicitly declaring a forbid context). PARSEK_ANALYZER_KEEP_STALE=1
        // maps to -KeepStaleBaselineEntries. Runs the analyzer in ignore internally
        // (to see the TRUE findings) then writes baseline.cfg beside the save.
        [Fact]
        [Trait("Category", "Manual")]
        public void Manual_WriteBaselineForEnvSave()
        {
            string saveDir = Environment.GetEnvironmentVariable("PARSEK_ANALYZER_SAVE");
            if (string.IsNullOrEmpty(saveDir))
                return; // env unset -> skip cleanly (CI-safe)

            // Structural refusal is the harness Forbid run; on top of that,
            // -WriteBaseline itself refuses only when a forbid context is declared.
            BaselineMode envMode = OfflineAnalyzer.ParseBaselineMode(
                Environment.GetEnvironmentVariable("PARSEK_ANALYZER_BASELINE_MODE"));
            Assert.False(envMode == BaselineMode.Forbid,
                "-WriteBaseline refuses to run in a declared forbid context");

            string resultsDir = Environment.GetEnvironmentVariable("PARSEK_ANALYZER_RESULTS");
            if (string.IsNullOrEmpty(resultsDir))
                resultsDir = DefaultResultsDir();

            bool keepStale = string.Equals(
                Environment.GetEnvironmentVariable("PARSEK_ANALYZER_KEEP_STALE"), "1", StringComparison.Ordinal);

            AnalysisBaseline written = OfflineAnalyzer.WriteBaselineForSave(saveDir, resultsDir, Resolver, keepStale);

            string baselinePath = OfflineAnalyzer.ResolveBaselinePath(saveDir);
            Assert.True(File.Exists(baselinePath), "-WriteBaseline must write baseline.cfg beside the save");
            Assert.NotNull(written);
        }

        // The known historical saves regression floor (design "Plumbing"). A standing
        // floor: green means "no new damage on any listed save"; any red is a
        // genuinely new finding on top of the already-baselined INV2 overlaps.
        //
        // This is the SINGLE Manual test scripts/analyze-historical-saves.ps1 drives
        // (via `dotnet test --filter`): the script resolves the existing save dirs and
        // exports them as a semicolon-separated PARSEK_ANALYZER_HISTORICAL_SAVES list,
        // and this Fact loops that list, running each save in Apply and collecting any
        // red. A Fact over an env list (rather than a [Theory] with baked-in
        // [InlineData] rows) is used because the save paths are machine-specific and
        // not committable. Each save's reports are written beside it (or under
        // PARSEK_ANALYZER_RESULTS), so the script reports per-save GREEN/RED from each
        // report's terminal RED= token. Skips cleanly when the env var is unset.
        [Fact]
        [Trait("Category", "Manual")]
        public void Manual_HistoricalSaves_GreenUnderApply()
        {
            string list = Environment.GetEnvironmentVariable("PARSEK_ANALYZER_HISTORICAL_SAVES");
            if (string.IsNullOrEmpty(list))
                return; // env unset -> skip cleanly

            string resultsRoot = Environment.GetEnvironmentVariable("PARSEK_ANALYZER_RESULTS");
            var reds = new System.Collections.Generic.List<string>();

            foreach (string raw in list.Split(new[] { ';' }, StringSplitOptions.RemoveEmptyEntries))
            {
                string save = raw.Trim();
                if (string.IsNullOrEmpty(save) || !Directory.Exists(save))
                    continue; // a missing listed save is skipped, not a red

                string resultsDir = string.IsNullOrEmpty(resultsRoot)
                    ? Path.Combine(save, "analysis")
                    : Path.Combine(resultsRoot, Path.GetFileName(save.TrimEnd(
                        Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar)));

                AnalysisReport report = OfflineAnalyzer.Run(save, resultsDir, Resolver, BaselineMode.Apply);
                if (report.IsRed)
                    reds.Add(save + " (failNonBaselined=" + report.Counts.FailNonBaselined
                        + " staleNonBaselined=" + report.Counts.StaleNonBaselined + ")");
            }

            Assert.True(reds.Count == 0,
                "historical saves with NEW (non-baselined) findings: " + string.Join(", ", reds));
        }
    }
}
