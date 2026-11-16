using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using Xunit;

namespace Parsek.Tests
{
    /// <summary>
    /// RecordingPaths.ResolveSaveRoot, the save-root accessor, and the file collector's KSP
    /// branch that returns it. Outside KSP, KSPUtil.ApplicationRootPath throws, so the
    /// accessor's save-context read always reports a missing context here.
    /// </summary>
    [Collection("Sequential")]
    public class RecordingPathsSaveRootTests : IDisposable
    {
        private const string MissingContextText = "ResolveSaveRoot missing context";

        private readonly List<string> logLines = new List<string>();
        private readonly string originalSaveFolder;
        private readonly string originalSaveRootOverride;
        private readonly string originalJanitorOverride;

        public RecordingPathsSaveRootTests()
        {
            ParsekLog.ResetTestOverrides();
            ParsekLog.SuppressLogging = false;
            ParsekLog.VerboseOverrideForTesting = true;
            ParsekLog.TestSinkForTesting = line => logLines.Add(line);
            ParsekFileCollector.ResetForTesting();

            originalSaveFolder = HighLogic.SaveFolder;
            originalSaveRootOverride = RecordingPaths.SaveRootOverrideForTesting;
            originalJanitorOverride = RecordingFileJanitor.CleanOrphanFilesDirectoryOverrideForTesting;
            RecordingPaths.SaveRootOverrideForTesting = null;
            RecordingFileJanitor.CleanOrphanFilesDirectoryOverrideForTesting = null;
        }

        public void Dispose()
        {
            HighLogic.SaveFolder = originalSaveFolder;
            RecordingPaths.SaveRootOverrideForTesting = originalSaveRootOverride;
            RecordingFileJanitor.CleanOrphanFilesDirectoryOverrideForTesting = originalJanitorOverride;
            ParsekFileCollector.ResetForTesting();
            ParsekLog.ResetTestOverrides();
            ParsekLog.SuppressLogging = true;
        }

        private int MissingContextLineCount()
        {
            return logLines.Count(line =>
                line.Contains("[VERBOSE][Paths]") && line.Contains(MissingContextText));
        }

        [Fact]
        public void Override_ReturnsItsFullPath()
        {
            string root = Path.Combine(Path.GetTempPath(), "parsek-save-root-" + Guid.NewGuid().ToString("N"));
            RecordingPaths.SaveRootOverrideForTesting = Path.Combine(root, "sub", "..");

            Assert.Equal(Path.GetFullPath(root), RecordingPaths.ResolveSaveRoot());
            Assert.Equal(0, MissingContextLineCount());
        }

        [Fact]
        public void NoSaveContext_ReturnsNullAndLogsTheMissingContextLineOnce()
        {
            HighLogic.SaveFolder = null;
            ParsekLog.ClockOverrideForTesting = () => 1000.0;

            Assert.Null(RecordingPaths.ResolveSaveRoot());
            Assert.Null(RecordingPaths.ResolveSaveRoot());

            Assert.Equal(1, MissingContextLineCount());
            string line = logLines.First(l => l.Contains(MissingContextText));
            Assert.Contains("rootSet=False", line);
            Assert.Contains("relativeSet=True", line);
            Assert.DoesNotContain(logLines, l => l.Contains("[WARN]") || l.Contains("[ERROR]"));
        }

        [Fact]
        public void Collector_KspBranch_ReturnsTheAccessor()
        {
            HighLogic.SaveFolder = "parsek-save-root-" + Guid.NewGuid().ToString("N");
            ParsekLog.ClockOverrideForTesting = () => 1000.0;

            Assert.Null(ParsekFileCollector.ResolveSaveDirectory());

            // The null comes from the accessor: its missing-context line is the witness. The
            // save flag is not asserted, because the Linux reference DLLs stub HighLogic.
            string line = logLines.Single(l => l.Contains(MissingContextText));
            Assert.Contains("rootSet=False", line);
        }

        [Fact]
        public void Collector_SaveRootOverride_MatchesTheAccessor()
        {
            string root = Path.Combine(Path.GetTempPath(), "parsek-save-root-" + Guid.NewGuid().ToString("N"));
            RecordingPaths.SaveRootOverrideForTesting = root;

            Assert.Equal(RecordingPaths.ResolveSaveRoot(), ParsekFileCollector.ResolveSaveDirectory());
            Assert.Equal(Path.GetFullPath(root), ParsekFileCollector.ResolveSaveDirectory());
        }

        [Fact]
        public void Collector_OwnOverrideStaysFirst()
        {
            string own = Path.Combine(Path.GetTempPath(), "parsek-collector-own-" + Guid.NewGuid().ToString("N"));
            ParsekFileCollector.SaveDirectoryOverrideForTesting = own;
            RecordingPaths.SaveRootOverrideForTesting =
                Path.Combine(Path.GetTempPath(), "parsek-save-root-" + Guid.NewGuid().ToString("N"));

            Assert.Equal(Path.GetFullPath(own), ParsekFileCollector.ResolveSaveDirectory());
            Assert.Equal(0, MissingContextLineCount());
        }
    }
}
