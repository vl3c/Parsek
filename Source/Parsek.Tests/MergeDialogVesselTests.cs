using System.Collections.Generic;
using Xunit;

namespace Parsek.Tests
{
    /// <summary>
    /// Tests for MergeDialog.CanPersistVessel and BuildDefaultVesselDecisions.
    /// </summary>
    [Collection("Sequential")]
    public class MergeDialogVesselTests : System.IDisposable
    {
        private readonly List<string> logLines = new List<string>();

        public MergeDialogVesselTests()
        {
            RecordingStore.SuppressLogging = true;
            RecordingStore.ResetForTesting();
            ParsekLog.ResetTestOverrides();
            ParsekLog.SuppressLogging = false;
            ParsekLog.TestSinkForTesting = line => logLines.Add(line);
            ParsekLog.VerboseOverrideForTesting = true;
        }

        public void Dispose()
        {
            ParsekLog.ResetTestOverrides();
            RecordingStore.ResetForTesting();
        }

        // ============================================================
        // CanPersistVessel
        // ============================================================

        [Fact]
        public void CanPersistVessel_Orbiting_ReturnsTrue()
        {
            var rec = new Recording
            {
                TerminalStateValue = TerminalState.Orbiting,
                VesselSnapshot = new ConfigNode("VESSEL")
            };
            Assert.True(MergeDialog.CanPersistVessel(rec));
        }

        [Fact]
        public void CanPersistVessel_Landed_ReturnsTrue()
        {
            var rec = new Recording
            {
                TerminalStateValue = TerminalState.Landed,
                VesselSnapshot = new ConfigNode("VESSEL")
            };
            Assert.True(MergeDialog.CanPersistVessel(rec));
        }

        [Fact]
        public void CanPersistVessel_Destroyed_ReturnsFalse()
        {
            var rec = new Recording
            {
                TerminalStateValue = TerminalState.Destroyed,
                VesselSnapshot = new ConfigNode("VESSEL")
            };
            Assert.False(MergeDialog.CanPersistVessel(rec));
        }

        [Fact]
        public void CanPersistVessel_Recovered_ReturnsFalse()
        {
            var rec = new Recording
            {
                TerminalStateValue = TerminalState.Recovered,
                VesselSnapshot = new ConfigNode("VESSEL")
            };
            Assert.False(MergeDialog.CanPersistVessel(rec));
        }

        [Fact]
        public void CanPersistVessel_Docked_ReturnsFalse()
        {
            var rec = new Recording
            {
                TerminalStateValue = TerminalState.Docked,
                VesselSnapshot = new ConfigNode("VESSEL")
            };
            Assert.False(MergeDialog.CanPersistVessel(rec));
        }

        [Fact]
        public void CanPersistVessel_NullTerminalState_ReturnsTrue()
        {
            var rec = new Recording
            {
                TerminalStateValue = null,
                VesselSnapshot = new ConfigNode("VESSEL")
            };
            Assert.True(MergeDialog.CanPersistVessel(rec));
        }

        [Fact]
        public void CanPersistVessel_NoVesselSnapshot_ReturnsFalse()
        {
            var rec = new Recording
            {
                TerminalStateValue = TerminalState.Orbiting,
                VesselSnapshot = null
            };
            Assert.False(MergeDialog.CanPersistVessel(rec));
        }

        [Fact]
        public void CanPersistVessel_NullRecording_ReturnsFalse()
        {
            Assert.False(MergeDialog.CanPersistVessel(null));
        }

        [Fact]
        public void CanPersistVessel_Boarded_ReturnsFalse()
        {
            var rec = new Recording
            {
                TerminalStateValue = TerminalState.Boarded,
                VesselSnapshot = new ConfigNode("VESSEL")
            };
            Assert.False(MergeDialog.CanPersistVessel(rec));
        }

        [Fact]
        public void CanPersistVessel_Splashed_ReturnsTrue()
        {
            var rec = new Recording
            {
                TerminalStateValue = TerminalState.Splashed,
                VesselSnapshot = new ConfigNode("VESSEL")
            };
            Assert.True(MergeDialog.CanPersistVessel(rec));
        }

        [Fact]
        public void CanPersistVessel_SubOrbital_ReturnsFalse()
        {
            var rec = new Recording
            {
                TerminalStateValue = TerminalState.SubOrbital,
                VesselSnapshot = new ConfigNode("VESSEL")
            };
            Assert.False(MergeDialog.CanPersistVessel(rec));
        }

        [Fact]
        public void CanPersistVessel_Debris_ReturnsFalse()
        {
            var rec = new Recording
            {
                TerminalStateValue = TerminalState.Orbiting,
                VesselSnapshot = new ConfigNode("VESSEL"),
                IsDebris = true
            };
            Assert.False(MergeDialog.CanPersistVessel(rec));
        }

        // ============================================================
        // BuildDefaultVesselDecisions
        // ============================================================

        [Fact]
        public void BuildDefaultVesselDecisions_TwoSurvivingOneDestroyed()
        {
            var tree = new RecordingTree { TreeName = "TestTree" };
            tree.Recordings["r1"] = new Recording
            {
                RecordingId = "r1",
                VesselName = "Capsule",
                TerminalStateValue = TerminalState.Orbiting,
                VesselSnapshot = new ConfigNode("VESSEL")
            };
            tree.Recordings["r2"] = new Recording
            {
                RecordingId = "r2",
                VesselName = "Booster",
                TerminalStateValue = TerminalState.Destroyed,
                VesselSnapshot = new ConfigNode("VESSEL")
            };
            tree.Recordings["r3"] = new Recording
            {
                RecordingId = "r3",
                VesselName = "Payload",
                TerminalStateValue = TerminalState.Landed,
                VesselSnapshot = new ConfigNode("VESSEL")
            };

            var decisions = MergeDialog.BuildDefaultVesselDecisions(tree);

            Assert.Equal(3, decisions.Count);
            Assert.True(decisions["r1"]);   // Capsule orbiting: persist
            Assert.False(decisions["r2"]);  // Booster destroyed: ghost-only
            Assert.True(decisions["r3"]);   // Payload landed: persist

            // Verify logging
            Assert.Contains(logLines, l => l.Contains("[MergeDialog]") && l.Contains("canPersist=True") && l.Contains("Capsule"));
            Assert.Contains(logLines, l => l.Contains("[MergeDialog]") && l.Contains("canPersist=False") && l.Contains("Booster"));
            Assert.Contains(logLines, l => l.Contains("[MergeDialog]") && l.Contains("canPersist=True") && l.Contains("Payload"));
        }

        [Fact]
        public void BuildDefaultVesselDecisions_EmptyTree_ReturnsEmptyDict()
        {
            var tree = new RecordingTree { TreeName = "Empty" };
            var decisions = MergeDialog.BuildDefaultVesselDecisions(tree);
            Assert.Empty(decisions);
        }

        [Fact]
        public void BuildDefaultVesselDecisions_NullTree_ReturnsEmptyDict()
        {
            var decisions = MergeDialog.BuildDefaultVesselDecisions(null);
            Assert.Empty(decisions);
        }

        [Fact]
        public void BuildDefaultVesselDecisions_AllDestroyed_AllGhostOnly()
        {
            var tree = new RecordingTree { TreeName = "Doomed" };
            tree.Recordings["r1"] = new Recording
            {
                RecordingId = "r1",
                VesselName = "Ship1",
                TerminalStateValue = TerminalState.Destroyed,
                VesselSnapshot = new ConfigNode("VESSEL")
            };
            tree.Recordings["r2"] = new Recording
            {
                RecordingId = "r2",
                VesselName = "Ship2",
                TerminalStateValue = TerminalState.Destroyed,
                VesselSnapshot = new ConfigNode("VESSEL")
            };

            var decisions = MergeDialog.BuildDefaultVesselDecisions(tree);

            Assert.Equal(2, decisions.Count);
            Assert.False(decisions["r1"]);
            Assert.False(decisions["r2"]);
        }

        [Fact]
        public void BuildDefaultVesselDecisions_DebrisAndSubOrbital_DefaultGhostOnly()
        {
            var tree = new RecordingTree { TreeName = "CrashDebris" };
            tree.Recordings["debris"] = new Recording
            {
                RecordingId = "debris",
                VesselName = "Booster Debris",
                TerminalStateValue = TerminalState.Orbiting,
                VesselSnapshot = new ConfigNode("VESSEL"),
                IsDebris = true
            };
            tree.Recordings["suborbital"] = new Recording
            {
                RecordingId = "suborbital",
                VesselName = "Capsule",
                TerminalStateValue = TerminalState.SubOrbital,
                VesselSnapshot = new ConfigNode("VESSEL")
            };

            var decisions = MergeDialog.BuildDefaultVesselDecisions(tree);

            Assert.Equal(2, decisions.Count);
            Assert.False(decisions["debris"]);
            Assert.False(decisions["suborbital"]);
        }

        [Fact]
        public void BuildDefaultVesselDecisions_SkipsNonLeafRecordings()
        {
            // Non-leaf recordings have ChildBranchPointId != null and are not
            // returned by GetAllLeaves, so they should not appear in decisions.
            var tree = new RecordingTree { TreeName = "Branched" };
            tree.Recordings["root"] = new Recording
            {
                RecordingId = "root",
                VesselName = "Root",
                ChildBranchPointId = "bp1",  // Not a leaf
                VesselSnapshot = new ConfigNode("VESSEL")
            };
            tree.Recordings["leaf"] = new Recording
            {
                RecordingId = "leaf",
                VesselName = "Leaf",
                TerminalStateValue = TerminalState.Orbiting,
                VesselSnapshot = new ConfigNode("VESSEL")
            };

            var decisions = MergeDialog.BuildDefaultVesselDecisions(tree);

            Assert.Single(decisions);
            Assert.True(decisions.ContainsKey("leaf"));
            Assert.False(decisions.ContainsKey("root"));
        }

        // MarkForceSpawnOnTreeRecordings tests removed — spawn dedup bypass is now
        // stateless via RecordingStore.SceneEntryActiveVesselPid (#226).
    }
}
