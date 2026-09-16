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
            LedgerOrchestrator.SetKerbalsForTesting(null);
            ParsekLog.ResetTestOverrides();
            ParsekLog.SuppressLogging = false;
            ParsekLog.TestSinkForTesting = line => logLines.Add(line);
        }

        public void Dispose()
        {
            LedgerOrchestrator.SetKerbalsForTesting(null);
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
            RecordingStore.AddRecordingWithTreeForTesting(rec);
            var kerbals = KerbalsTestHelper.RecalculateFromStore();

            // Inject the module so ShouldSuppressCrewStatusChange can find it
            LedgerOrchestrator.SetKerbalsForTesting(kerbals);

            Assert.True(kerbals.IsManaged("Jeb"), "Jeb should be managed");
            Assert.True(GameStateRecorder.ShouldSuppressCrewStatusChange(
                "Jeb", suppressFlag: false, isIdentity: false));
        }

        [Fact]
        public void ShouldSuppress_UnmanagedKerbal_ReturnsFalse()
        {
            // The module must be PRESENT and simply not manage the queried name: with no
            // module injected the static call only reaches the `?? false` fallback, so
            // IsManaged is never consulted and an IsManaged answering true for every name
            // would keep this green. Jeb is the managed one here, Val is the query.
            var rec = new Recording
            {
                RecordingId = "rec-crew",
                VesselSnapshot = BuildSnapshotWithCrew("Jeb")
            };
            rec.CrewEndStates = new Dictionary<string, KerbalEndState>
            {
                { "Jeb", KerbalEndState.Aboard }
            };
            RecordingStore.AddRecordingWithTreeForTesting(rec);
            var kerbals = KerbalsTestHelper.RecalculateFromStore();
            LedgerOrchestrator.SetKerbalsForTesting(kerbals);

            Assert.NotNull(LedgerOrchestrator.Kerbals);
            Assert.True(kerbals.IsManaged("Jeb"), "Jeb should be managed");
            Assert.False(kerbals.IsManaged("Val"), "Val should not be managed");
            Assert.False(GameStateRecorder.ShouldSuppressCrewStatusChange(
                "Val", suppressFlag: false, isIdentity: false));
        }

        [Fact]
        public void ShouldSuppress_NoKerbalsModule_ReturnsFalse()
        {
            // The null-module fallback, kept as its own cell: with no module injected the
            // `?? false` arm decides and nothing is suppressed.
            Assert.Null(LedgerOrchestrator.Kerbals);
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
            var kerbals = new KerbalsModule();
            Assert.False(kerbals.IsManaged("Jeb"),
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
