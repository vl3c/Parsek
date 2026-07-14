using System.Collections.Generic;
using Parsek.TestCommands;
using Xunit;

namespace Parsek.Tests
{
    /// <summary>
    /// Pure coverage for the dispatcher's static surface (P3.1): the per-verb
    /// precondition table and the ITestCommandExecutor contract. Guards that every
    /// implemented v1 verb has a precondition entry with the right scene requirement
    /// (a missing/wrong entry would let a recorder verb execute outside FLIGHT) and
    /// that the executor interface exposes exactly one method per v1 verb.
    /// </summary>
    public class TestCommandDispatchStateTests
    {
        /// <summary>Minimal fake executor used to prove the interface shape and that a
        /// dispatched verb routes to exactly one method. Records the last call.</summary>
        private sealed class FakeExecutor : ITestCommandExecutor
        {
            public readonly List<string> Calls = new List<string>();
            public void SetSetting(ParsedCommand cmd) => Calls.Add("SetSetting");
            public void StartRecording(ParsedCommand cmd) => Calls.Add("StartRecording");
            public void StopRecording(ParsedCommand cmd) => Calls.Add("StopRecording");
            public void CommitTree(ParsedCommand cmd) => Calls.Add("CommitTree");
            public void DiscardTree(ParsedCommand cmd) => Calls.Add("DiscardTree");
            public void RecordingState(ParsedCommand cmd) => Calls.Add("RecordingState");
            public void RunTests(ParsedCommand cmd) => Calls.Add("RunTests");
            public void LoadGame(ParsedCommand cmd) => Calls.Add("LoadGame");
            public void MissionMark(ParsedCommand cmd) => Calls.Add("MissionMark");
            public void FlushAndQuit(ParsedCommand cmd) => Calls.Add("FlushAndQuit");
            public void InvokeRewind(ParsedCommand cmd) => Calls.Add("InvokeRewind");
            public void AnswerMergeDialog(ParsedCommand cmd) => Calls.Add("AnswerMergeDialog");
            public void TimeJump(ParsedCommand cmd) => Calls.Add("TimeJump");
            public void KscAction(ParsedCommand cmd) => Calls.Add("KscAction");
        }

        [Fact]
        public void PreconditionTable_CoversAllImplementedVerbs()
        {
            foreach (string verb in TestCommandVerbs.ImplementedVerbNames)
            {
                Assert.True(TestCommandDispatcher.PreconditionTable.ContainsKey(verb),
                    $"no precondition entry for implemented verb {verb}");
            }
            Assert.Equal(TestCommandVerbs.ImplementedVerbNames.Count,
                TestCommandDispatcher.PreconditionTable.Count);
        }

        // Expected requirement passed as a name string because the internal enum
        // cannot appear in a public xUnit theory signature.
        [Theory]
        [InlineData("StartRecording", "RequiresFlight")]
        [InlineData("StopRecording", "RequiresFlight")]
        [InlineData("CommitTree", "RequiresFlight")]
        [InlineData("DiscardTree", "RequiresFlight")]
        [InlineData("SetSetting", "RequiresGameLoaded")]
        [InlineData("RecordingState", "AnyScene")]
        [InlineData("RunTests", "AnyScene")]
        [InlineData("LoadGame", "AnyScene")]
        [InlineData("MissionMark", "AnyScene")]
        [InlineData("FlushAndQuit", "AnyScene")]
        [InlineData("InvokeRewind", "RequiresFlight")]
        [InlineData("AnswerMergeDialog", "AnyScene")]
        [InlineData("TimeJump", "RequiresFlight")]
        [InlineData("KscAction", "AnyScene")]
        public void RequirementFor_MatchesTable(string verb, string expected)
        {
            Assert.Equal(expected, TestCommandDispatcher.RequirementFor(verb).ToString());
        }

        [Fact]
        public void Executor_HasOneMethodPerImplementedVerb()
        {
            var fake = new FakeExecutor();
            var cmd = new ParsedCommand();
            fake.SetSetting(cmd);
            fake.StartRecording(cmd);
            fake.StopRecording(cmd);
            fake.CommitTree(cmd);
            fake.DiscardTree(cmd);
            fake.RecordingState(cmd);
            fake.RunTests(cmd);
            fake.LoadGame(cmd);
            fake.MissionMark(cmd);
            fake.FlushAndQuit(cmd);
            fake.InvokeRewind(cmd);
            fake.AnswerMergeDialog(cmd);
            fake.TimeJump(cmd);
            fake.KscAction(cmd);

            // One interface method per implemented v1 verb, no more, no less.
            var interfaceMethods = typeof(ITestCommandExecutor).GetMethods();
            Assert.Equal(TestCommandVerbs.ImplementedVerbNames.Count, interfaceMethods.Length);
            foreach (string verb in TestCommandVerbs.ImplementedVerbNames)
                Assert.Contains(fake.Calls, c => c == verb);
        }
    }
}
