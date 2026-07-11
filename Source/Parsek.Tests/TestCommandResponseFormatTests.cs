using System.Collections.Generic;
using Parsek.TestCommands;
using Xunit;

namespace Parsek.Tests
{
    /// <summary>
    /// Pure coverage for the response-line formatter
    /// (<see cref="TestCommandResponse.FormatResponseLine"/>). Guards the exact
    /// grep-able shape (id/cmd/verdict/seq present, ut omitted with no game,
    /// payload values percent-encoded, InvariantCulture floats). A regression here
    /// drops a field or leaks a locale comma into ut, which the orchestrator
    /// cannot parse.
    /// </summary>
    public class TestCommandResponseFormatTests
    {
        [Fact]
        public void Format_WithGameLoaded_IncludesUtAndPayload()
        {
            string line = TestCommandResponse.FormatResponseLine(
                "0002", "StartRecording", "OK", 2, 1234.5,
                new[] { new KeyValuePair<string, string>("recordingId", "abc123") },
                null);
            Assert.Equal("id=0002 cmd=StartRecording verdict=OK seq=2 ut=1234.5 recordingId=abc123", line);
        }

        [Fact]
        public void Format_NoGame_OmitsUt()
        {
            string line = TestCommandResponse.FormatResponseLine(
                "0001", "SetSetting", "OK", 1, null,
                new[] { new KeyValuePair<string, string>("name", "autoMerge") },
                null);
            Assert.DoesNotContain(" ut=", line);
            Assert.StartsWith("id=0001 cmd=SetSetting verdict=OK seq=1 name=autoMerge", line);
        }

        [Fact]
        public void Format_RejectMsg_IsPercentEncoded()
        {
            string line = TestCommandResponse.FormatResponseLine(
                "0009", "SetSetting", "REJECTED", 9, null, null,
                "setting-not-whitelisted name=foo");
            // Space and '=' inside the msg must be escaped so the reader keeps it
            // on one token.
            Assert.Contains("msg=setting-not-whitelisted%20name%3Dfoo", line);
        }

        [Fact]
        public void Format_FloatUt_UsesInvariantCulture()
        {
            string line = TestCommandResponse.FormatResponseLine(
                "1", "RecordingState", "OK", 3, 1235.1, null, null);
            Assert.Contains("ut=1235.1", line);
            Assert.DoesNotContain(",", line); // no comma-locale decimal
        }

        [Fact]
        public void Format_FieldOrder_IsIdCmdVerdictSeqUtPayloadMsg()
        {
            string line = TestCommandResponse.FormatResponseLine(
                "5", "RunTests", "OK", 5, 1300.0,
                new[]
                {
                    new KeyValuePair<string, string>("passed", "42"),
                    new KeyValuePair<string, string>("failed", "0"),
                },
                "note");
            int idIdx = line.IndexOf("id=");
            int cmdIdx = line.IndexOf(" cmd=");
            int verdictIdx = line.IndexOf(" verdict=");
            int seqIdx = line.IndexOf(" seq=");
            int utIdx = line.IndexOf(" ut=");
            int passedIdx = line.IndexOf(" passed=");
            int msgIdx = line.IndexOf(" msg=");
            Assert.True(idIdx < cmdIdx && cmdIdx < verdictIdx && verdictIdx < seqIdx
                        && seqIdx < utIdx && utIdx < passedIdx && passedIdx < msgIdx);
        }

        [Fact]
        public void Format_NoTrailingNewline()
        {
            string line = TestCommandResponse.FormatResponseLine(
                "1", "StopRecording", "OK", 1, null, null, null);
            Assert.DoesNotContain("\n", line);
        }
    }
}
