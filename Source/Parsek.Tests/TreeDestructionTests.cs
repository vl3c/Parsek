using System;
using System.Collections.Generic;
using Xunit;

namespace Parsek.Tests
{
    [Collection("Sequential")]
    public class TreeDestructionTests : IDisposable
    {
        private readonly List<string> logLines = new List<string>();

        public TreeDestructionTests()
        {
            RecordingStore.SuppressLogging = true;
            MilestoneStore.ResetForTesting();
            GameStateStore.SuppressLogging = true;
            ParsekLog.ResetTestOverrides();
            ParsekLog.SuppressLogging = false;
            ParsekLog.TestSinkForTesting = line => logLines.Add(line);
        }

        public void Dispose()
        {
            ParsekLog.ResetTestOverrides();
            ParsekLog.SuppressLogging = true;
            RecordingStore.SuppressLogging = true;
        }

        #region Helpers

        static Recording MakeLeaf(string id, string name, TerminalState? terminal)
        {
            return new Recording
            {
                RecordingId = id,
                VesselName = name,
                ChildBranchPointId = null,
                TerminalStateValue = terminal
            };
        }

        static Recording MakeNonLeaf(string id, string name, string childBranchPointId)
        {
            return new Recording
            {
                RecordingId = id,
                VesselName = name,
                ChildBranchPointId = childBranchPointId,
                TerminalStateValue = null
            };
        }

        static Dictionary<string, Recording> MakeDict(
            params Recording[] recordings)
        {
            var dict = new Dictionary<string, Recording>();
            foreach (var rec in recordings)
                dict[rec.RecordingId] = rec;
            return dict;
        }

        #endregion

        #region AreAllLeavesTerminal

        [Fact]
        public void AllLeavesDestroyed_ReturnsTrue()
        {
            var recs = MakeDict(
                MakeLeaf("a", "Rocket A", TerminalState.Destroyed),
                MakeLeaf("b", "Rocket B", TerminalState.Destroyed));

            bool result = RecordingTree.AreAllLeavesTerminal(recs, null, true);

            Assert.True(result);
        }

        [Fact]
        public void OneLeafNullTerminal_ReturnsFalse()
        {
            var recs = MakeDict(
                MakeLeaf("a", "Rocket A", TerminalState.Destroyed),
                MakeLeaf("b", "Rocket B", null));

            bool result = RecordingTree.AreAllLeavesTerminal(recs, null, true);

            Assert.False(result);
        }

        [Fact]
        public void MixedTerminal_DestroyedAndRecovered_ReturnsTrue()
        {
            var recs = MakeDict(
                MakeLeaf("a", "Rocket A", TerminalState.Destroyed),
                MakeLeaf("b", "Capsule B", TerminalState.Recovered));

            bool result = RecordingTree.AreAllLeavesTerminal(recs, null, true);

            Assert.True(result);
        }

        [Fact]
        public void ActiveRecordingAlive_ReturnsFalse()
        {
            var recs = MakeDict(
                MakeLeaf("active", "Rocket Active", null),
                MakeLeaf("bg", "Booster", TerminalState.Destroyed));

            // Active recording is alive (activeVesselDestroyed = false)
            bool result = RecordingTree.AreAllLeavesTerminal(recs, "active", false);

            Assert.False(result);
        }

        [Fact]
        public void ActiveRecordingDestroyed_ReturnsTrue()
        {
            var recs = MakeDict(
                MakeLeaf("active", "Rocket Active", TerminalState.Destroyed),
                MakeLeaf("bg", "Booster", TerminalState.Destroyed));

            // Active recording destroyed
            bool result = RecordingTree.AreAllLeavesTerminal(recs, "active", true);

            Assert.True(result);
        }

        [Fact]
        public void ActiveRecordingDestroyed_NullTerminalState_ReturnsTrue()
        {
            // Critical case: active recording has null TerminalStateValue (not yet
            // finalized) but activeVesselDestroyed is true — should be treated as terminal
            var recs = MakeDict(
                MakeLeaf("active", "Rocket Active", null),
                MakeLeaf("bg", "Booster", TerminalState.Destroyed));

            bool result = RecordingTree.AreAllLeavesTerminal(recs, "active", true);

            Assert.True(result);
            Assert.Contains(logLines, l =>
                l.Contains("[TreeDestruction]")
                && l.Contains("leaves=2")
                && l.Contains("terminal=2")
                && l.Contains("alive=0")
                && l.Contains("True"));
        }

        [Fact]
        public void NonLeafWithoutTerminal_Ignored_ReturnsTrue()
        {
            // Non-leaf recording (has ChildBranchPointId) should be ignored
            var recs = MakeDict(
                MakeNonLeaf("parent", "Root Vessel", "bp_1"),
                MakeLeaf("child", "Stage 2", TerminalState.Destroyed));

            bool result = RecordingTree.AreAllLeavesTerminal(recs, null, true);

            Assert.True(result);
        }

        [Fact]
        public void EmptyRecordings_ReturnsTrue()
        {
            var recs = new Dictionary<string, Recording>();

            bool result = RecordingTree.AreAllLeavesTerminal(recs, null, true);

            Assert.True(result);
        }

        [Fact]
        public void LeafWithOrbiting_ReturnsFalse()
        {
            var recs = MakeDict(
                MakeLeaf("a", "Orbiter", TerminalState.Orbiting),
                MakeLeaf("b", "Booster", TerminalState.Destroyed));

            bool result = RecordingTree.AreAllLeavesTerminal(recs, null, true);

            Assert.False(result);
        }

        [Fact]
        public void LeafWithLanded_ReturnsFalse()
        {
            var recs = MakeDict(
                MakeLeaf("a", "Lander", TerminalState.Landed));

            bool result = RecordingTree.AreAllLeavesTerminal(recs, null, true);

            Assert.False(result);
        }

        [Fact]
        public void AllNonSpawnableTerminals_ReturnsTrue()
        {
            // All four non-spawnable terminal states
            var recs = MakeDict(
                MakeLeaf("a", "Rocket A", TerminalState.Destroyed),
                MakeLeaf("b", "Capsule B", TerminalState.Recovered),
                MakeLeaf("c", "Docked C", TerminalState.Docked),
                MakeLeaf("d", "Kerbal D", TerminalState.Boarded));

            bool result = RecordingTree.AreAllLeavesTerminal(recs, null, true);

            Assert.True(result);
        }

        [Fact]
        public void LeafWithSubOrbital_ReturnsFalse()
        {
            var recs = MakeDict(
                MakeLeaf("a", "SubOrbiter", TerminalState.SubOrbital));

            bool result = RecordingTree.AreAllLeavesTerminal(recs, null, true);

            Assert.False(result);
        }

        [Fact]
        public void LeafWithSplashed_ReturnsFalse()
        {
            var recs = MakeDict(
                MakeLeaf("a", "Swimmer", TerminalState.Splashed));

            bool result = RecordingTree.AreAllLeavesTerminal(recs, null, true);

            Assert.False(result);
        }

        [Fact]
        public void LoggingTest_SummarizesLeavesOnce()
        {
            var recs = MakeDict(
                MakeLeaf("a", "Rocket A", TerminalState.Destroyed),
                MakeNonLeaf("parent", "Root", "bp_1"),
                MakeLeaf("b", "Orbiter B", TerminalState.Orbiting));

            logLines.Clear();

            bool result = RecordingTree.AreAllLeavesTerminal(recs, null, true);

            Assert.False(result);
            Assert.Single(logLines);
            Assert.Contains(logLines, l =>
                l.Contains("[TreeDestruction]")
                && l.Contains("AreAllLeavesTerminal")
                && l.Contains("leaves=2")
                && l.Contains("terminal=1")
                && l.Contains("alive=1")
                && l.Contains("False"));
            Assert.DoesNotContain(logLines, l => l.Contains("Rocket A"));
            Assert.DoesNotContain(logLines, l => l.Contains("Orbiter B"));
        }

        [Fact]
        public void ActiveRecording_NoTerminalState_NotDestroyed_ReturnsFalse()
        {
            // Active recording with no terminal state, vessel NOT destroyed
            var recs = MakeDict(
                MakeLeaf("active", "My Rocket", null));

            bool result = RecordingTree.AreAllLeavesTerminal(recs, "active", false);

            Assert.False(result);
            Assert.Contains(logLines, l =>
                l.Contains("[TreeDestruction]")
                && l.Contains("leaves=1")
                && l.Contains("terminal=0")
                && l.Contains("alive=1")
                && l.Contains("False"));
        }

        [Fact]
        public void ActiveCrashBlockers_DebrisLeafWithNullTerminal_ReturnsTrue()
        {
            var recs = MakeDict(
                MakeLeaf("active", "Rocket Active", null),
                new Recording
                {
                    RecordingId = "debris",
                    VesselName = "Booster Debris",
                    ChildBranchPointId = null,
                    TerminalStateValue = null,
                    IsDebris = true
                });

            bool result = RecordingTree.AreAllActiveCrashBlockersDebris(recs, "active");

            Assert.True(result);
        }

        [Fact]
        public void ActiveCrashBlockers_NonDebrisLeafWithNullTerminal_ReturnsFalse()
        {
            var recs = MakeDict(
                MakeLeaf("active", "Rocket Active", null),
                MakeLeaf("survivor", "Probe Core", null));

            bool result = RecordingTree.AreAllActiveCrashBlockersDebris(recs, "active");

            Assert.False(result);
        }

        [Fact]
        public void ActiveCrashBlockers_DebrisLeafWithSpawnableTerminal_ReturnsTrue()
        {
            var recs = MakeDict(
                MakeLeaf("active", "Rocket Active", null),
                new Recording
                {
                    RecordingId = "debris",
                    VesselName = "Fairing Debris",
                    ChildBranchPointId = null,
                    TerminalStateValue = TerminalState.SubOrbital,
                    IsDebris = true
                });

            bool result = RecordingTree.AreAllActiveCrashBlockersDebris(recs, "active");

            Assert.True(result);
        }

        [Fact]
        public void ActiveCrashBlockers_NonDebrisLeafWithSpawnableTerminal_ReturnsFalse()
        {
            var recs = MakeDict(
                MakeLeaf("active", "Rocket Active", null),
                MakeLeaf("survivor", "Recovery Capsule", TerminalState.Orbiting));

            bool result = RecordingTree.AreAllActiveCrashBlockersDebris(recs, "active");

            Assert.False(result);
        }

        #endregion
    }
}
