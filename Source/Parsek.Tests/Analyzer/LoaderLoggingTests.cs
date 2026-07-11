using System;
using System.Collections.Generic;
using System.IO;
using Parsek;
using Parsek.Tests.Generators;
using Xunit;

namespace Parsek.Tests.Analyzer
{
    // Log-assertion tests for the loader's diagnostic lines (design "Diagnostic
    // Logging"). Uses ParsekLog.TestSinkForTesting to capture emitted lines and
    // asserts the code path executed and logged the expected data. Sequential:
    // touches the ParsekLog + RecordingStore static state.
    [Collection("Sequential")]
    public class LoaderLoggingTests : IDisposable
    {
        private readonly string tempDir;
        private readonly bool prevSuppress;
        private readonly List<string> logLines = new List<string>();

        public LoaderLoggingTests()
        {
            prevSuppress = RecordingStore.SuppressLogging;
            RecordingStore.SuppressLogging = false;
            RecordingStore.ResetForTesting();

            // Capture ParsekLog output; keep Verbose enabled so the (a) summary
            // (Verbose) is observable, and do NOT suppress ParsekLog (the loader
            // suppresses RecordingStore's own logs internally, not ParsekLog).
            ParsekLog.SuppressLogging = false;
            ParsekLog.VerboseOverrideForTesting = true;
            ParsekLog.TestSinkForTesting = line => logLines.Add(line);

            tempDir = Path.Combine(Path.GetTempPath(),
                "parsek-analyzer-loaderlog-" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(tempDir);
        }

        public void Dispose()
        {
            ParsekLog.ResetTestOverrides();
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

        // Guards (design "Diagnostic Logging" (a)): a clean load emits one Verbose
        // per-subject summary line carrying the save name + tree / recording / fault
        // counts. Fails if the loader stops logging its summary (the run log would
        // lose the per-subject one-shot that ties findings to a subject).
        [Fact]
        public void Load_EmitsVerboseLoadSummaryLine()
        {
            string saveDir = NewSaveDir("logsummary");
            var writer = new ScenarioWriter().AddRecordingAsTree(
                new RecordingBuilder("Solo").WithRecordingId("solo0").AddPoint(100, 0, 0, 1000));
            string scenarioText = writer.SerializeConfigNode(writer.BuildScenarioNode(), "SCENARIO", 1);
            File.WriteAllText(Path.Combine(saveDir, "persistent.sfs"),
                "GAME\n{\n\tFLIGHTSTATE\n\t{\n\t\tversion = 1.12.5\n\t}\n" + scenarioText + "}\n");

            SaveDirectoryLoader.Load(saveDir, NullResolver);

            Assert.Contains(logLines, l =>
                l.Contains("[VERBOSE][Analyzer]")
                && l.Contains("load save='logsummary'")
                && l.Contains("trees=1")
                && l.Contains("recordings=1")
                && l.Contains("loadFaults=0"));
        }

        // Guards (design "Diagnostic Logging" (b)): every LoadFault emits a Warn line
        // naming the file kind + reason. Fails if a parse failure is silent in the
        // run log (the "a file that failed to parse is itself a finding" contract
        // would lose its observable trace).
        [Fact]
        public void Load_EmitsWarnLinePerLoadFault()
        {
            string saveDir = NewSaveDir("logfault");
            File.WriteAllText(Path.Combine(saveDir, "persistent.sfs"),
                "GAME\n{\n\tSCENARIO\n\t{\n\t\tname = ParsekScenario\n{{{ broken unbalanced");

            SaveDirectoryLoader.Load(saveDir, NullResolver);

            Assert.Contains(logLines, l =>
                l.Contains("[WARN][Analyzer]")
                && l.Contains("loadFault kind=sfs")
                && l.Contains("reason="));
        }
    }
}
