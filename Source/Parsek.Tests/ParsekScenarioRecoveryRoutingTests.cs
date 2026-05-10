using Xunit;

namespace Parsek.Tests
{
    [Collection("Sequential")]
    public class ParsekScenarioRecoveryRoutingTests : System.IDisposable
    {
        public ParsekScenarioRecoveryRoutingTests()
        {
            RecordingStore.SuppressLogging = true;
            RecordingStore.ResetForTesting();
        }

        public void Dispose()
        {
            RecordingStore.ResetForTesting();
            RecordingStore.SuppressLogging = false;
        }

        [Fact]
        public void ShouldPatchRecoveryFundsOutsideFlight_NoPendingOwner_ReturnsTrue()
        {
            Assert.True(ParsekScenario.ShouldPatchRecoveryFundsOutsideFlight(
                GameScenes.SPACECENTER,
                "Recovered Probe"));
        }

        [Fact]
        public void ShouldPatchRecoveryFundsOutsideFlight_PendingTreeOwnsVessel_ReturnsFalse()
        {
            var rec = new Recording
            {
                RecordingId = "pending-owned-recovery",
                VesselName = "Recovered Probe",
                PreLaunchFunds = 50000.0,
                TerminalStateValue = TerminalState.Orbiting
            };
            rec.Points.Add(new TrajectoryPoint { ut = 100.0, funds = 40000.0 });
            rec.Points.Add(new TrajectoryPoint { ut = 200.0, funds = 40000.0 });
            RecordingStore.StashPendingTree(MakePendingTree(rec));

            Assert.False(ParsekScenario.ShouldPatchRecoveryFundsOutsideFlight(
                GameScenes.SPACECENTER,
                "Recovered Probe"));
        }

        [Fact]
        public void ShouldPatchRecoveryFundsOutsideFlight_NormalizedPendingOwner_ReturnsFalse()
        {
            var rec = new Recording
            {
                RecordingId = "pending-localized-recovery",
                VesselName = "Jumping Flea",
                PreLaunchFunds = 50000.0,
                TerminalStateValue = TerminalState.Orbiting
            };
            rec.Points.Add(new TrajectoryPoint { ut = 100.0, funds = 40000.0 });
            rec.Points.Add(new TrajectoryPoint { ut = 200.0, funds = 40000.0 });
            RecordingStore.StashPendingTree(MakePendingTree(rec));

            Assert.False(ParsekScenario.ShouldPatchRecoveryFundsOutsideFlight(
                GameScenes.SPACECENTER,
                RecoveredVesselIdentity.FromNames("#autoLOC_501224", "Jumping Flea")));
        }

        [Fact]
        public void ShouldPatchRecoveryFundsOutsideFlight_GhostOnlyPendingMatch_DoesNotBlock()
        {
            var rec = new Recording
            {
                RecordingId = "pending-ghost-only",
                VesselName = "Recovered Probe",
                IsGhostOnly = true
            };
            rec.Points.Add(new TrajectoryPoint { ut = 100.0, funds = 0.0 });
            rec.Points.Add(new TrajectoryPoint { ut = 200.0, funds = 0.0 });
            RecordingStore.StashPendingTree(MakePendingTree(rec));

            Assert.True(ParsekScenario.ShouldPatchRecoveryFundsOutsideFlight(
                GameScenes.SPACECENTER,
                "Recovered Probe"));
        }

        [Fact]
        public void ShouldPatchRecoveryFundsOutsideFlight_FlightScene_ReturnsFalse()
        {
            Assert.False(ParsekScenario.ShouldPatchRecoveryFundsOutsideFlight(
                GameScenes.FLIGHT,
                "Recovered Probe"));
        }

        [Fact]
        public void UpdateRecordingsForTerminalEvent_NormalizedName_UpdatesPendingOwner()
        {
            var rec = new Recording
            {
                RecordingId = "pending-localized-terminal",
                VesselName = "Jumping Flea",
                PreLaunchFunds = 50000.0,
                TerminalStateValue = TerminalState.Orbiting
            };
            rec.Points.Add(new TrajectoryPoint { ut = 100.0, funds = 40000.0 });
            rec.Points.Add(new TrajectoryPoint { ut = 200.0, funds = 40000.0 });
            RecordingStore.StashPendingTree(MakePendingTree(rec));

            bool updated = ParsekScenario.UpdateRecordingsForTerminalEvent(
                RecoveredVesselIdentity.FromNames("#autoLOC_501224", "Jumping Flea"),
                TerminalState.Recovered,
                288.7);

            Assert.True(updated);
            Assert.Equal(TerminalState.Recovered, rec.TerminalStateValue);
            Assert.Equal(288.7, rec.ExplicitEndUT);
        }

        private static RecordingTree MakePendingTree(Recording rec)
        {
            var tree = new RecordingTree
            {
                Id = "pending-tree-" + rec.RecordingId,
                TreeName = rec.VesselName ?? rec.RecordingId,
                RootRecordingId = rec.RecordingId
            };
            rec.TreeId = tree.Id;
            tree.AddOrReplaceRecording(rec);
            return tree;
        }
    }
}
