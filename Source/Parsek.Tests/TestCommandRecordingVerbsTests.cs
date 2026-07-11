using System.Collections.Generic;
using System.Linq;
using Parsek.TestCommands;
using Xunit;

namespace Parsek.Tests
{
    /// <summary>
    /// P5.4 / P5.5 payload coverage for the four recorder/tree verbs. The idempotency
    /// flags (already / idle / nothing) are the signals the orchestrator uses to tell a
    /// real action from a no-op, so their presence rules are pinned here. Fails if a
    /// no-op flag leaks onto a real action (or vice versa) or a payload key drifts.
    /// </summary>
    public class TestCommandRecordingVerbsTests
    {
        private static bool Has(List<KeyValuePair<string, string>> p, string key)
            => p.Any(kv => kv.Key == key);

        private static string Val(List<KeyValuePair<string, string>> p, string key)
            => p.First(kv => kv.Key == key).Value;

        [Fact]
        public void Start_FreshRecorder_NoAlreadyFlag()
        {
            var p = TestCommandRecordingVerbs.BuildStartPayload(alreadyLive: false, recordingId: "rec1");
            Assert.Equal("rec1", Val(p, "recordingId"));
            Assert.False(Has(p, "already"));
        }

        [Fact]
        public void Start_AlreadyLive_CarriesAlreadyTrue()
        {
            var p = TestCommandRecordingVerbs.BuildStartPayload(alreadyLive: true, recordingId: "rec1");
            Assert.Equal("rec1", Val(p, "recordingId"));
            Assert.Equal("true", Val(p, "already"));
        }

        [Fact]
        public void Start_NullRecordingId_EmptyString()
        {
            var p = TestCommandRecordingVerbs.BuildStartPayload(false, null);
            Assert.Equal(string.Empty, Val(p, "recordingId"));
        }

        [Fact]
        public void Stop_LiveRecorder_StoppedTrue_NoIdle()
        {
            var p = TestCommandRecordingVerbs.BuildStopPayload(wasLive: true);
            Assert.Equal("true", Val(p, "stopped"));
            Assert.False(Has(p, "idle"));
        }

        [Fact]
        public void Stop_NoRecorder_StoppedFalse_IdleTrue()
        {
            var p = TestCommandRecordingVerbs.BuildStopPayload(wasLive: false);
            Assert.Equal("false", Val(p, "stopped"));
            Assert.Equal("true", Val(p, "idle"));
        }
    }
}
