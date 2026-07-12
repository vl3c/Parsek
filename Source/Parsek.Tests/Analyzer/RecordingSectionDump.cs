using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Text;
using Parsek;
using Xunit;
using Xunit.Abstractions;

namespace Parsek.Tests.Analyzer
{
    // Permanent triage tool (design doc "Run modes" / "Ad hoc triage"): dumps the
    // loaded TrackSection kinds/spans + rewind-save state for one recording (or all
    // recordings) in a save directory, so a human can ground-truth an INV2 overlap
    // or an INV9 missing-rewind finding against the ACTUAL section referenceFrame /
    // environment / source / seam status instead of guessing from the report line.
    //
    // Loads through the same SaveDirectoryLoader the analyzer uses, so it sees the
    // identical hydrated model the rules see. Reads:
    //   PARSEK_DUMP_SAVE      (required) - absolute path to a save directory
    //   PARSEK_DUMP_RECORDING (optional) - recording id to focus; unset = summarize all
    //   PARSEK_DUMP_RESULTS   (optional) - directory the dump file is written to
    //
    // SKIPS CLEANLY when PARSEK_DUMP_SAVE is unset so it never runs in the normal CI
    // pass. Output goes to the xUnit test log (ITestOutputHelper) AND to a
    // <resultsDir>/<recordingId|all>.sectiondump.txt file for later inspection.
    //
    // The tool is READ-ONLY over the triaged save: it NEVER writes into the save
    // directory (the earlier behavior wrote <save>/analysis/*.sectiondump.txt inside
    // the save under triage, which the analyzer results-dir convention forbids). The
    // dump file lands in, in order of precedence: PARSEK_DUMP_RESULTS, else alongside
    // the analyzer's own output at PARSEK_ANALYZER_RESULTS, else the test output dir
    // (AppContext.BaseDirectory/section-dumps, i.e. bin/Debug/net472/section-dumps).
    [Collection("Sequential")]
    public class RecordingSectionDump : IDisposable
    {
        private readonly ITestOutputHelper output;
        private readonly bool prevSuppress;

        public RecordingSectionDump(ITestOutputHelper output)
        {
            this.output = output;
            prevSuppress = RecordingStore.SuppressLogging;
            RecordingStore.SuppressLogging = true;
            RecordingStore.ResetForTesting();
            ParsekLog.SuppressLogging = true;
        }

        public void Dispose()
        {
            RecordingStore.ResetForTesting();
            RecordingStore.SuppressLogging = prevSuppress;
        }

        private static CelestialBody Resolver(string name) => TestBodyRegistry.CreateBody(name);

        [Fact]
        [Trait("Category", "Manual")]
        public void Manual_DumpRecordingSections()
        {
            string saveDir = Environment.GetEnvironmentVariable("PARSEK_DUMP_SAVE");
            if (string.IsNullOrEmpty(saveDir))
                return; // env unset -> skip cleanly (CI-safe)

            string focusId = Environment.GetEnvironmentVariable("PARSEK_DUMP_RECORDING");

            AnalyzerModel model = SaveDirectoryLoader.Load(saveDir, Resolver);
            var sb = new StringBuilder();
            var ic = CultureInfo.InvariantCulture;

            sb.AppendLine(Inv("[SectionDump] save='{0}' recordings={1} loadFaults={2} focus={3}",
                model.SaveName, model.Recordings.Count, model.LoadFaults.Count,
                string.IsNullOrEmpty(focusId) ? "<all>" : focusId));

            foreach (Recording rec in model.Recordings)
            {
                if (rec == null)
                    continue;
                if (!string.IsNullOrEmpty(focusId)
                    && !string.Equals(rec.RecordingId, focusId, StringComparison.Ordinal))
                    continue;

                DumpRecording(rec, model, sb, !string.IsNullOrEmpty(focusId));
            }

            string text = sb.ToString();
            output.WriteLine(text);

            // Persist to the analyzer results-dir convention, NEVER inside the save
            // under triage (the tool stays read-only over saves).
            try
            {
                string resultsDir = ResolveResultsDir();
                Directory.CreateDirectory(resultsDir);
                string name = string.IsNullOrEmpty(focusId) ? "all" : SafeName(focusId);
                string dumpPath = Path.Combine(resultsDir, name + ".sectiondump.txt");
                File.WriteAllText(dumpPath, text);
                output.WriteLine("[SectionDump] wrote " + dumpPath);
            }
            catch (Exception ex)
            {
                output.WriteLine("[SectionDump] failed to write dump file: " + ex.Message);
            }
        }

        // Output directory, in precedence order: PARSEK_DUMP_RESULTS, else the
        // analyzer's own results dir (PARSEK_ANALYZER_RESULTS), else the test output
        // dir (AppContext.BaseDirectory/section-dumps). Never the save directory.
        private static string ResolveResultsDir()
        {
            string dumpResults = Environment.GetEnvironmentVariable("PARSEK_DUMP_RESULTS");
            if (!string.IsNullOrEmpty(dumpResults))
                return dumpResults;

            string analyzerResults = Environment.GetEnvironmentVariable("PARSEK_ANALYZER_RESULTS");
            if (!string.IsNullOrEmpty(analyzerResults))
                return analyzerResults;

            return Path.Combine(AppContext.BaseDirectory, "section-dumps");
        }

        private static void DumpRecording(Recording rec, AnalyzerModel model, StringBuilder sb, bool verbose)
        {
            int sectionCount = rec.TrackSections?.Count ?? 0;
            int orbitCount = rec.OrbitSegments?.Count ?? 0;
            string rwState = RewindSaveState(rec, model.SaveDirectory);

            sb.AppendLine(Inv(
                "  REC id={0} merge={1} sections={2} orbits={3} parent={4} chain={5}/{6} rewindSave={7} [{8}]",
                rec.RecordingId,
                rec.MergeState,
                sectionCount,
                orbitCount,
                rec.ParentRecordingId ?? "<none>",
                rec.ChainId ?? "<none>",
                rec.ChainIndex,
                rec.RewindSaveFileName ?? "<none>",
                rwState));

            if (!verbose || rec.TrackSections == null)
                return;

            for (int i = 0; i < rec.TrackSections.Count; i++)
            {
                TrackSection s = rec.TrackSections[i];
                int frames = s.frames?.Count ?? 0;
                int bodyFixed = s.bodyFixedFrames?.Count ?? 0;
                int checkpoints = s.checkpoints?.Count ?? 0;
                sb.AppendLine(Inv(
                    "    #{0,-3} ref={1,-16} env={2,-16} src={3,-10} seam={4} ut=[{5},{6}] dur={7} frames={8} bodyFixed={9} checkpoints={10} anchorRec={11} anchorVessel={12}",
                    i,
                    s.referenceFrame,
                    s.environment,
                    s.source,
                    s.isBoundarySeam ? 1 : 0,
                    s.startUT.ToString("R", CultureInfo.InvariantCulture),
                    s.endUT.ToString("R", CultureInfo.InvariantCulture),
                    (s.endUT - s.startUT).ToString("F3", CultureInfo.InvariantCulture),
                    frames,
                    bodyFixed,
                    checkpoints,
                    s.anchorRecordingId ?? "<none>",
                    s.anchorVesselId));
            }

            if (rec.OrbitSegments != null)
            {
                for (int i = 0; i < rec.OrbitSegments.Count; i++)
                {
                    OrbitSegment o = rec.OrbitSegments[i];
                    sb.AppendLine(Inv(
                        "    ORB #{0,-3} ut=[{1},{2}] dur={3}",
                        i,
                        o.startUT.ToString("R", CultureInfo.InvariantCulture),
                        o.endUT.ToString("R", CultureInfo.InvariantCulture),
                        (o.endUT - o.startUT).ToString("F3", CultureInfo.InvariantCulture)));
                }
            }
        }

        private static string RewindSaveState(Recording rec, string saveDir)
        {
            if (string.IsNullOrEmpty(rec.RewindSaveFileName))
                return "no-rewind-save";
            if (string.IsNullOrEmpty(saveDir))
                return "save-dir-null";
            string rel = RecordingPaths.BuildRewindSaveRelativePath(rec.RewindSaveFileName);
            string path = Path.Combine(saveDir, rel);
            return File.Exists(path) ? "SAVES-PRESENT" : "SAVES-MISSING";
        }

        private static string SafeName(string id)
        {
            var sb = new StringBuilder();
            foreach (char c in id)
                sb.Append(char.IsLetterOrDigit(c) || c == '-' || c == '_' ? c : '_');
            return sb.ToString();
        }

        private static string Inv(string format, params object[] args) =>
            string.Format(CultureInfo.InvariantCulture, format, args);
    }
}
