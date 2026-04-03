using System;
using System.Collections.Generic;
using Xunit;

namespace Parsek.Tests
{
    [Collection("Sequential")]
    public class CrewStatusSuppressionTests : IDisposable
    {
        private readonly List<string> logLines = new List<string>();

        public CrewStatusSuppressionTests()
        {
            RecordingStore.SuppressLogging = true;
            RecordingStore.ResetForTesting();
            KerbalsModule.ResetForTesting();
            ParsekLog.ResetTestOverrides();
            ParsekLog.SuppressLogging = false;
            ParsekLog.TestSinkForTesting = line => logLines.Add(line);
        }

        public void Dispose()
        {
            KerbalsModule.ResetForTesting();
            RecordingStore.ResetForTesting();
            RecordingStore.SuppressLogging = false;
            ParsekLog.ResetTestOverrides();
            ParsekLog.SuppressLogging = true;
        }

        // ================================================================
        // ShouldSuppressCrewStatusChange — core decision logic
        // ================================================================

        [Fact]
        public void ShouldSuppress_SuppressFlagTrue_ReturnsTrue()
        {
            Assert.True(GameStateRecorder.ShouldSuppressCrewStatusChange(
                "Jeb", suppressFlag: true, isIdentity: false));
        }

        [Fact]
        public void ShouldSuppress_IdentityTransition_ReturnsTrue()
        {
            Assert.True(GameStateRecorder.ShouldSuppressCrewStatusChange(
                "Jeb", suppressFlag: false, isIdentity: true));
        }

        [Fact]
        public void ShouldSuppress_ManagedKerbal_ReturnsTrue()
        {
            // Setup: make "Jeb" a managed kerbal via reservation
            var rec = new Recording
            {
                RecordingId = "rec-crew",
                VesselSnapshot = BuildSnapshotWithCrew("Jeb")
            };
            rec.CrewEndStates = new Dictionary<string, KerbalEndState>
            {
                { "Jeb", KerbalEndState.Aboard }
            };
            RecordingStore.AddCommittedForTesting(rec);
            KerbalsModule.Recalculate();

            Assert.True(KerbalsModule.IsManaged("Jeb"), "Jeb should be managed");
            Assert.True(GameStateRecorder.ShouldSuppressCrewStatusChange(
                "Jeb", suppressFlag: false, isIdentity: false));
        }

        [Fact]
        public void ShouldSuppress_UnmanagedKerbal_ReturnsFalse()
        {
            // No recordings, no reservations — "Val" is not managed
            Assert.False(KerbalsModule.IsManaged("Val"));
            Assert.False(GameStateRecorder.ShouldSuppressCrewStatusChange(
                "Val", suppressFlag: false, isIdentity: false));
        }

        [Fact]
        public void ShouldSuppress_NullName_ReturnsFalse()
        {
            Assert.False(GameStateRecorder.ShouldSuppressCrewStatusChange(
                null, suppressFlag: false, isIdentity: false));
        }

        [Fact]
        public void ShouldSuppress_AllFlagsOff_ReturnsFalse()
        {
            Assert.False(GameStateRecorder.ShouldSuppressCrewStatusChange(
                "Bob", suppressFlag: false, isIdentity: false));
        }

        // ================================================================
        // Regression: initial boarding event NOT suppressed
        // ================================================================

        [Fact]
        public void ShouldSuppress_CrewBeforeReservation_NotSuppressed()
        {
            // Before any tree is committed, crew boards the vessel.
            // At this point they are NOT managed — the Available->Assigned
            // event must NOT be suppressed.
            // No recordings committed = no reservations
            Assert.False(KerbalsModule.IsManaged("Jeb"),
                "Jeb should not be managed before reservation");
            Assert.False(GameStateRecorder.ShouldSuppressCrewStatusChange(
                "Jeb", suppressFlag: false, isIdentity: false),
                "Initial boarding event should not be suppressed");
        }

        private static ConfigNode BuildSnapshotWithCrew(params string[] crewNames)
        {
            var vessel = new ConfigNode("VESSEL");
            var part = vessel.AddNode("PART");
            for (int i = 0; i < crewNames.Length; i++)
                part.AddValue("crew", crewNames[i]);
            return vessel;
        }
    }
}
