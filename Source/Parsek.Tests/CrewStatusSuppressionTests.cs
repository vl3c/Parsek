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

        /// <summary>
        /// Creates a KerbalsModule instance, runs the full lifecycle against
        /// RecordingStore.CommittedRecordings, and returns the populated module.
        /// </summary>
        private static KerbalsModule RecalculateFromStore()
        {
            var module = new KerbalsModule();
            module.Reset();

            var actions = new List<GameAction>();
            var recordings = RecordingStore.CommittedRecordings;
            for (int i = 0; i < recordings.Count; i++)
            {
                var rec = recordings[i];
                if (rec.CrewEndStates == null && rec.VesselSnapshot != null)
                    KerbalsModule.PopulateCrewEndStates(rec);

                var snapshot = rec.GhostVisualSnapshot ?? rec.VesselSnapshot;
                if (snapshot == null) continue;
                var names = CrewReservationManager.ExtractCrewFromSnapshot(snapshot);
                for (int j = 0; j < names.Count; j++)
                {
                    KerbalEndState endState = KerbalEndState.Unknown;
                    if (rec.CrewEndStates != null)
                        rec.CrewEndStates.TryGetValue(names[j], out endState);

                    actions.Add(new GameAction
                    {
                        UT = rec.StartUT,
                        Type = GameActionType.KerbalAssignment,
                        RecordingId = rec.RecordingId,
                        KerbalName = names[j],
                        KerbalRole = KerbalsModule.FindTraitForKerbal(names[j]),
                        StartUT = (float)rec.StartUT,
                        EndUT = (float)rec.EndUT,
                        KerbalEndStateField = endState,
                        Sequence = j + 1
                    });
                }
            }

            module.PrePass(actions);
            for (int i = 0; i < actions.Count; i++)
                module.ProcessAction(actions[i]);
            module.PostWalk();
            return module;
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
            var kerbals = RecalculateFromStore();

            // Inject the module so ShouldSuppressCrewStatusChange can find it
            LedgerOrchestrator.SetKerbalsForTesting(kerbals);

            Assert.True(kerbals.IsManaged("Jeb"), "Jeb should be managed");
            Assert.True(GameStateRecorder.ShouldSuppressCrewStatusChange(
                "Jeb", suppressFlag: false, isIdentity: false));
        }

        [Fact]
        public void ShouldSuppress_UnmanagedKerbal_ReturnsFalse()
        {
            // No recordings, no reservations — "Val" is not managed
            var kerbals = new KerbalsModule();
            Assert.False(kerbals.IsManaged("Val"));
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
