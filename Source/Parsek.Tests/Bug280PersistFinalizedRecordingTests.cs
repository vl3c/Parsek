using System;
using System.Collections.Generic;
using Xunit;

namespace Parsek.Tests
{
    /// <summary>
    /// Tests for <see cref="BackgroundRecorder.PersistFinalizedRecording"/> — the
    /// helper introduced for bug #280 that writes a background recording's .prec
    /// sidecar immediately at finalization time, bypassing OnSave's FilesDirty
    /// round-trip. The helper's contract is thin (null-check + SaveRecordingFiles
    /// + success/failure logging), but the logging contract is load-bearing: the
    /// next playtest will rely on the `wrote sidecar for recId=…` / `failed to
    /// write sidecar` lines to confirm the fix is active and working.
    ///
    /// The happy-path write (live KSP save folder) is exercised via the in-game
    /// playtest — unit tests cover the null-safety and failure-logging paths that
    /// don't need a live KSP runtime.
    /// </summary>
    [Collection("Sequential")]
    public class Bug280PersistFinalizedRecordingTests : IDisposable
    {
        private readonly List<string> logLines = new List<string>();

        public Bug280PersistFinalizedRecordingTests()
        {
            RecordingStore.SuppressLogging = true;
            ParsekLog.ResetTestOverrides();
            ParsekLog.SuppressLogging = false;
            ParsekLog.TestSinkForTesting = line => logLines.Add(line);
            ParsekLog.VerboseOverrideForTesting = true;
        }

        public void Dispose()
        {
            ParsekLog.ResetTestOverrides();
            ParsekLog.SuppressLogging = true;
            RecordingStore.SuppressLogging = true;
        }

        [Fact]
        public void PersistFinalizedRecording_NullRec_IsNoOpAndEmitsNoLog()
        {
            BackgroundRecorder.PersistFinalizedRecording(null, "null-test");

            // No log lines should be emitted for the null-guard early return.
            foreach (var line in logLines)
            {
                Assert.DoesNotContain("PersistFinalizedRecording", line);
            }
        }

        [Fact]
        public void PersistFinalizedRecording_InvalidRecordingId_LogsFailureAtWarn()
        {
            // An empty RecordingId is rejected by RecordingPaths.ValidateRecordingId,
            // causing SaveRecordingFiles to return false. PersistFinalizedRecording
            // must report the failure at Warn level with the context string so the
            // next playtest log has a diagnostic breadcrumb if a save fails.
            var rec = new Recording
            {
                RecordingId = "", // invalid
                VesselName = "test"
            };

            BackgroundRecorder.PersistFinalizedRecording(rec, "TestContext ctx=42");

            Assert.Contains(logLines, l =>
                l.Contains("[Parsek][WARN][BgRecorder]") &&
                l.Contains("PersistFinalizedRecording: failed to write sidecar") &&
                l.Contains("TestContext ctx=42"));

            // Verify no success log fired.
            Assert.DoesNotContain(logLines, l =>
                l.Contains("PersistFinalizedRecording: wrote sidecar"));
        }

        [Fact]
        public void PersistFinalizedRecording_InvalidRecordingId_IncludesRecordingIdInFailureLog()
        {
            // The failure log must include the (attempted) recording id for traceability.
            // Even empty, it should appear in the "recId=" fragment so the log format is
            // stable across null and valid values.
            var rec = new Recording
            {
                RecordingId = "not-a-valid-guid-just-a-string",
                VesselName = "test"
            };

            BackgroundRecorder.PersistFinalizedRecording(rec, "traceability-test");

            Assert.Contains(logLines, l =>
                l.Contains("PersistFinalizedRecording: failed to write sidecar") &&
                l.Contains("recId=not-a-valid-guid-just-a-string"));
        }

        [Fact]
        public void PersistFinalizedRecording_ContextString_IsIncludedInBothSuccessAndFailureLogs()
        {
            // The context string is the diagnostic breadcrumb that identifies WHICH
            // finalization site triggered the write (destroy / shutdown / etc.). It
            // must be present in every log line so next-playtest triage can match
            // the log entry back to the source code site.
            var rec = new Recording
            {
                RecordingId = "../../etc/passwd", // path traversal — rejected by ValidateRecordingId
                VesselName = "test"
            };

            BackgroundRecorder.PersistFinalizedRecording(
                rec,
                "OnBackgroundVesselWillDestroy pid=12345");

            Assert.Contains(logLines, l =>
                l.Contains("OnBackgroundVesselWillDestroy pid=12345"));
        }
    }
}
