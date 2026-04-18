using System;
using System.Collections.Generic;
using Xunit;

namespace Parsek.Tests
{
    [Collection("Sequential")]
    public class Bug446GloopsDiscardNreTests : IDisposable
    {
        private readonly List<string> logLines = new List<string>();

        public Bug446GloopsDiscardNreTests()
        {
            ParsekLog.ResetTestOverrides();
            ParsekLog.SuppressLogging = false;
            ParsekLog.TestSinkForTesting = line => logLines.Add(line);
        }

        public void Dispose()
        {
            ParsekLog.ResetTestOverrides();
            ParsekLog.SuppressLogging = true;
        }

        [Theory]
        [InlineData(true, false, 0)]
        [InlineData(true, true, 0)]
        [InlineData(false, true, 1)]
        [InlineData(false, false, 2)]
        public void SelectStatusBlock_UsesExpectedTruthTable(
            bool isRecording,
            bool hasLastRecording,
            int expectedBlock)
        {
            var block = GloopsRecorderUI.SelectStatusBlock(isRecording, hasLastRecording);
            Assert.Equal((GloopsRecorderUI.StatusBlock)expectedBlock, block);
        }

        [Fact]
        public void RefreshStatusSnapshot_DiscardSavedRecording_PicksEmpty_AndLogsTransition()
        {
            var before = GloopsRecorderUI.CaptureStatusSnapshot(
                isRecording: false,
                hasLastRecording: true,
                isPreviewing: false);

            var after = GloopsRecorderUI.RefreshStatusSnapshot(
                before,
                isRecording: false,
                hasLastRecording: false,
                isPreviewing: false,
                buttonFired: true);

            Assert.Equal(GloopsRecorderUI.StatusBlock.Empty, after.Block);
            Assert.Contains(logLines, l =>
                l.Contains("Gloops state changed mid-DrawWindow")
                && l.Contains("hasLastRecording True->False"));
        }

        [Fact]
        public void RefreshStatusSnapshot_StartNewRecordingFromSaved_PicksRecording_AndLogsTransition()
        {
            var before = GloopsRecorderUI.CaptureStatusSnapshot(
                isRecording: false,
                hasLastRecording: true,
                isPreviewing: false);

            var after = GloopsRecorderUI.RefreshStatusSnapshot(
                before,
                isRecording: true,
                hasLastRecording: false,
                isPreviewing: false,
                buttonFired: true);

            Assert.Equal(GloopsRecorderUI.StatusBlock.Recording, after.Block);
            Assert.Contains(logLines, l =>
                l.Contains("Gloops state changed mid-DrawWindow")
                && l.Contains("isRecording False->True")
                && l.Contains("hasLastRecording True->False"));
        }

        [Fact]
        public void RefreshStatusSnapshot_StartPreview_LogsPreviewTransition()
        {
            var before = GloopsRecorderUI.CaptureStatusSnapshot(
                isRecording: false,
                hasLastRecording: true,
                isPreviewing: false);

            var after = GloopsRecorderUI.RefreshStatusSnapshot(
                before,
                isRecording: false,
                hasLastRecording: true,
                isPreviewing: true,
                buttonFired: true);

            Assert.Equal(GloopsRecorderUI.StatusBlock.Saved, after.Block);
            Assert.Contains(logLines, l =>
                l.Contains("Gloops state changed mid-DrawWindow")
                && l.Contains("isPreviewing False->True"));
        }

        [Fact]
        public void RefreshStatusSnapshot_ButtonFiredWithoutStateChange_DoesNotLogTransition()
        {
            var before = GloopsRecorderUI.CaptureStatusSnapshot(
                isRecording: false,
                hasLastRecording: true,
                isPreviewing: false);

            var after = GloopsRecorderUI.RefreshStatusSnapshot(
                before,
                isRecording: false,
                hasLastRecording: true,
                isPreviewing: false,
                buttonFired: true);

            Assert.Equal(GloopsRecorderUI.StatusBlock.Saved, after.Block);
            Assert.DoesNotContain(logLines, l => l.Contains("Gloops state changed mid-DrawWindow"));
        }
    }
}
