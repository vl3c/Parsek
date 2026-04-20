using System;
using System.Collections.Generic;
using System.IO;
using Xunit;

namespace Parsek.Tests
{
    [Collection("Sequential")]
    public class RecordingPathsLoggingTests : IDisposable
    {
        public RecordingPathsLoggingTests()
        {
            ParsekLog.ResetTestOverrides();
            ParsekLog.SuppressLogging = false;
            ParsekLog.VerboseOverrideForTesting = true;
        }

        public void Dispose()
        {
            ParsekLog.ResetTestOverrides();
            ParsekLog.SuppressLogging = true;
        }

        [Fact]
        public void ValidateRecordingId_ProductionContextLogsWarnForRejectedId()
        {
            var lines = new List<string>();
            ParsekLog.TestSinkForTesting = line => lines.Add(line);

            Assert.False(RecordingPaths.ValidateRecordingId("../etc/passwd"));

            Assert.Single(lines);
            Assert.Equal(
                "[Parsek][WARN][Paths] Recording id validation failed for '../etc/passwd': contains invalid path sequence",
                lines[0]);
        }

        [Fact]
        public void ValidateRecordingId_TestContextLogsVerboseForRejectedId()
        {
            var lines = new List<string>();
            ParsekLog.TestSinkForTesting = line => lines.Add(line);

            Assert.False(RecordingPaths.ValidateRecordingId(
                string.Empty,
                RecordingIdValidationLogContext.Test));

            Assert.Single(lines);
            Assert.Equal(
                "[Parsek][VERBOSE][Paths] Recording id validation failed: id is null or empty",
                lines[0]);
        }

        [Fact]
        public void ValidateRecordingId_TestContextLogsVerboseForInvalidFileNameChar()
        {
            var lines = new List<string>();
            ParsekLog.TestSinkForTesting = line => lines.Add(line);
            char invalidChar = GetInvalidFileNameCharForTesting();
            string invalidId = $"abc{invalidChar}def";

            Assert.False(RecordingPaths.ValidateRecordingId(
                invalidId,
                RecordingIdValidationLogContext.Test));

            Assert.Single(lines);
            Assert.Equal(
                $"[Parsek][VERBOSE][Paths] Recording id validation failed for '{invalidId}': contains invalid file-name char",
                lines[0]);
        }

        private static char GetInvalidFileNameCharForTesting()
        {
            char[] invalidChars = Path.GetInvalidFileNameChars();
            char[] preferredChars = { '<', '>', ':', '"', '|', '?', '*' };
            for (int i = 0; i < preferredChars.Length; i++)
            {
                if (Array.IndexOf(invalidChars, preferredChars[i]) >= 0)
                    return preferredChars[i];
            }

            return invalidChars[0];
        }
    }
}
