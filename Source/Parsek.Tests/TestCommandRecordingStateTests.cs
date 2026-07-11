using System.Collections.Generic;
using System.Linq;
using Parsek.TestCommands;
using Xunit;

namespace Parsek.Tests
{
    /// <summary>
    /// P5.2 payload coverage for <see cref="TestCommandRecordingState.BuildPayload"/>.
    /// RecordingState is read-only and valid in any scene; the load-bearing branch is
    /// the no-flight-instance collapse (recording=false, empty tree, zero points) so a
    /// menu / space-center RecordingState never reports a stale live recorder. Fails if
    /// a field name drifts or the no-flight collapse leaks a stale tree/points value.
    /// </summary>
    public class TestCommandRecordingStateTests
    {
        private static string Val(List<KeyValuePair<string, string>> p, string key)
            => p.First(kv => kv.Key == key).Value;

        [Fact]
        public void NoFlight_CollapsesToNotRecording_RegardlessOfOtherInputs()
        {
            var p = TestCommandRecordingState.BuildPayload(
                hasFlight: false, isRecording: true, treeId: "abc", points: 99, sceneName: "MAINMENU");

            Assert.Equal("false", Val(p, "recording"));
            Assert.Equal(string.Empty, Val(p, "tree"));
            Assert.Equal("0", Val(p, "points"));
            Assert.Equal("MAINMENU", Val(p, "scene"));
        }

        [Fact]
        public void Flight_Recording_ReportsSnapshotFields()
        {
            var p = TestCommandRecordingState.BuildPayload(
                hasFlight: true, isRecording: true, treeId: "tree7", points: 812, sceneName: "FLIGHT");

            Assert.Equal("true", Val(p, "recording"));
            Assert.Equal("tree7", Val(p, "tree"));
            Assert.Equal("812", Val(p, "points"));
            Assert.Equal("FLIGHT", Val(p, "scene"));
        }

        [Fact]
        public void Flight_NotRecording_NullTree_EmptyString()
        {
            var p = TestCommandRecordingState.BuildPayload(
                hasFlight: true, isRecording: false, treeId: null, points: 0, sceneName: "FLIGHT");

            Assert.Equal("false", Val(p, "recording"));
            Assert.Equal(string.Empty, Val(p, "tree"));
            Assert.Equal("0", Val(p, "points"));
        }

        [Fact]
        public void Payload_HasExactlyTheFourContractKeys_InOrder()
        {
            var p = TestCommandRecordingState.BuildPayload(true, true, "t", 1, "FLIGHT");
            Assert.Equal(new[] { "recording", "tree", "points", "scene" }, p.Select(kv => kv.Key).ToArray());
        }
    }
}
