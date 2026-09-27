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

        // catches: the name-only match, under which a stock KSC-declutter autoclean of an
        // unrelated "<Craft> Debris" (a different vessel, different pid) stamped every
        // same-named pending debris recording Recovered and nulled its snapshot.
        [Fact]
        public void UpdateRecordingsForTerminalEvent_SameNameDifferentPid_LeavesPendingRecording()
        {
            var rec = MakeNamedPendingDebris("pending-debris-a", 4242u, guid: null);
            RecordingStore.StashPendingTree(MakePendingTree(rec));

            bool updated = ParsekScenario.UpdateRecordingsForTerminalEvent(
                RecoveredVesselIdentity.FromRawName("Flea Debris"),
                TerminalState.Recovered,
                300.0,
                vesselPid: 9999u);

            Assert.False(updated);
            Assert.Equal(TerminalState.Orbiting, rec.TerminalStateValue);
            Assert.NotNull(rec.VesselSnapshot);
        }

        [Fact]
        public void UpdateRecordingsForTerminalEvent_SameNameSamePid_UpdatesPendingRecording()
        {
            var rec = MakeNamedPendingDebris("pending-debris-b", 4242u, guid: null);
            RecordingStore.StashPendingTree(MakePendingTree(rec));

            bool updated = ParsekScenario.UpdateRecordingsForTerminalEvent(
                RecoveredVesselIdentity.FromRawName("Flea Debris"),
                TerminalState.Recovered,
                300.0,
                vesselPid: 4242u);

            Assert.True(updated);
            Assert.Equal(TerminalState.Recovered, rec.TerminalStateValue);
            Assert.Null(rec.VesselSnapshot);
        }

        // catches: a craft-baked pid reused by a relaunch of the same craft; the launch guid
        // is what tells the two launches apart.
        [Fact]
        public void UpdateRecordingsForTerminalEvent_SamePidConclusivelyDifferentGuid_LeavesPendingRecording()
        {
            var rec = MakeNamedPendingDebris(
                "pending-debris-c", 4242u, guid: "0123456789abcdef0123456789abcdef");
            RecordingStore.StashPendingTree(MakePendingTree(rec));

            bool updated = ParsekScenario.UpdateRecordingsForTerminalEvent(
                RecoveredVesselIdentity.FromRawName(
                    "Flea Debris", "fedcba9876543210fedcba9876543210"),
                TerminalState.Recovered,
                300.0,
                vesselPid: 4242u);

            Assert.False(updated);
            Assert.Equal(TerminalState.Orbiting, rec.TerminalStateValue);
        }

        // An unknown pid on either side keeps the historical name-only behavior.
        [Theory]
        [InlineData(0u, 4242u)]
        [InlineData(4242u, 0u)]
        public void UpdateRecordingsForTerminalEvent_UnknownPid_FallsBackToName(
            uint recordingPid, uint recoveredPid)
        {
            var rec = MakeNamedPendingDebris("pending-debris-d", recordingPid, guid: null);
            RecordingStore.StashPendingTree(MakePendingTree(rec));

            bool updated = ParsekScenario.UpdateRecordingsForTerminalEvent(
                RecoveredVesselIdentity.FromRawName("Flea Debris"),
                TerminalState.Recovered,
                300.0,
                vesselPid: recoveredPid);

            Assert.True(updated);
            Assert.Equal(TerminalState.Recovered, rec.TerminalStateValue);
        }

        // catches: the funds routing and the terminal update disagreeing - a same-named
        // pending recording of a DIFFERENT vessel must not hold back the outside-FLIGHT
        // recovery payout, since that recording will never be stamped Recovered.
        [Fact]
        public void ShouldPatchRecoveryFundsOutsideFlight_SameNameDifferentPid_Patches()
        {
            var rec = MakeNamedPendingDebris("pending-debris-e", 4242u, guid: null);
            RecordingStore.StashPendingTree(MakePendingTree(rec));

            Assert.True(ParsekScenario.ShouldPatchRecoveryFundsOutsideFlight(
                GameScenes.SPACECENTER,
                RecoveredVesselIdentity.FromRawName("Flea Debris"),
                9999u));
            Assert.False(ParsekScenario.ShouldPatchRecoveryFundsOutsideFlight(
                GameScenes.SPACECENTER,
                RecoveredVesselIdentity.FromRawName("Flea Debris"),
                4242u));
        }

        private static Recording MakeNamedPendingDebris(string id, uint pid, string guid)
        {
            var rec = new Recording
            {
                RecordingId = id,
                VesselName = "Flea Debris",
                VesselPersistentId = pid,
                RecordedVesselGuid = guid,
                IsDebris = true,
                TerminalStateValue = TerminalState.Orbiting,
                VesselSnapshot = new ConfigNode("VESSEL")
            };
            rec.Points.Add(new TrajectoryPoint { ut = 100.0 });
            rec.Points.Add(new TrajectoryPoint { ut = 200.0 });
            return rec;
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
