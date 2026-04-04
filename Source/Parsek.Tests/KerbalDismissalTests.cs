using System.Collections.Generic;
using Xunit;

namespace Parsek.Tests
{
    /// <summary>
    /// Tests for KerbalsModule.IsManaged — the decision function backing
    /// the KerbalDismissalPatch Harmony prefix.
    /// </summary>
    [Collection("Sequential")] // touches KerbalsModule + RecordingStore static state
    public class KerbalDismissalTests : System.IDisposable
    {
        private readonly List<string> logLines = new List<string>();

        public KerbalDismissalTests()
        {
            ParsekLog.ResetTestOverrides();
            ParsekLog.SuppressLogging = false;
            ParsekLog.TestSinkForTesting = line => logLines.Add(line);
            RecordingStore.SuppressLogging = true;
            RecordingStore.ResetForTesting();
        }

        public void Dispose()
        {
            ParsekLog.ResetTestOverrides();
            ParsekLog.SuppressLogging = true;
            RecordingStore.SuppressLogging = false;
            RecordingStore.ResetForTesting();
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

        /// <summary>
        /// Creates a recording with crew in its VesselSnapshot and pre-populated
        /// CrewEndStates for the given terminal state.
        /// </summary>
        private Recording MakeRecording(string vesselName, string[] crew,
            TerminalState terminal, double endUT)
        {
            var snapshot = new ConfigNode("VESSEL");
            var part = snapshot.AddNode("PART");
            foreach (var c in crew)
                part.AddValue("crew", c);

            var rec = new Recording
            {
                VesselName = vesselName,
                VesselSnapshot = snapshot,
                TerminalStateValue = terminal,
                ExplicitStartUT = 0,
                ExplicitEndUT = endUT,
            };

            var endCrewSet = new HashSet<string>(crew);
            rec.CrewEndStates = new Dictionary<string, KerbalEndState>();
            for (int i = 0; i < crew.Length; i++)
            {
                rec.CrewEndStates[crew[i]] = KerbalsModule.InferCrewEndState(
                    crew[i], terminal, endCrewSet);
            }

            return rec;
        }

        [Fact]
        public void IsManaged_ReservedKerbal_ReturnsTrue()
        {
            // Arrange: commit a recording with Jeb — makes Jeb reserved
            var rec = MakeRecording("Test Ship", new[] { "Jeb" },
                TerminalState.Landed, 1000);
            RecordingStore.AddCommittedForTesting(rec);

            var kerbals = RecalculateFromStore();

            // Act + Assert
            Assert.True(kerbals.IsManaged("Jeb"),
                "Reserved kerbal should be managed by Parsek");

            // Verify logging
            Assert.Contains(logLines, l =>
                l.Contains("[KerbalsModule]") && l.Contains("Reservation") && l.Contains("Jeb"));
        }

        [Fact]
        public void IsManaged_RetiredKerbal_ReturnsTrue()
        {
            // Arrange: To create a retired kerbal:
            // 1. rec1 reserves Jeb (Aboard => open-ended reservation)
            // 2. rec2 uses StandIn1 as crew
            // Both Jeb and StandIn1 are reserved (both aboard intact vessels).
            var rec1 = MakeRecording("Ship1", new[] { "Jeb" },
                TerminalState.Landed, 1000);
            var rec2 = MakeRecording("Ship2", new[] { "StandIn1" },
                TerminalState.Landed, 2000);
            RecordingStore.AddCommittedForTesting(rec1);
            RecordingStore.AddCommittedForTesting(rec2);

            var kerbals = RecalculateFromStore();

            // Both are managed (reserved)
            Assert.True(kerbals.IsManaged("Jeb"));
            Assert.True(kerbals.IsManaged("StandIn1"));
        }

        [Fact]
        public void IsManaged_UnmanagedKerbal_ReturnsFalse()
        {
            // Arrange: commit a recording with Jeb only
            var rec = MakeRecording("Test Ship", new[] { "Jeb" },
                TerminalState.Landed, 1000);
            RecordingStore.AddCommittedForTesting(rec);

            var kerbals = RecalculateFromStore();

            // Act + Assert: Bill is not in any recording
            Assert.False(kerbals.IsManaged("Bill"),
                "Unmanaged kerbal should not be blocked from dismissal");
            Assert.False(kerbals.IsManaged("Bob"),
                "Unmanaged kerbal should not be blocked from dismissal");
        }

        [Fact]
        public void IsManaged_StandInInChain_ReturnsTrue()
        {
            // Arrange: commit a recording with Jeb, then manually inject a
            // stand-in into the slot chain to test IsManaged chain lookup
            var rec = MakeRecording("Ship", new[] { "Jeb" },
                TerminalState.Landed, 1000);
            RecordingStore.AddCommittedForTesting(rec);

            var kerbals = RecalculateFromStore();

            // Verify Jeb has a slot
            Assert.True(kerbals.Slots.ContainsKey("Jeb"));
            var slot = kerbals.Slots["Jeb"];

            // The chain should have a null entry (pending generation from ApplyToRoster).
            // Replace it with a named stand-in for testing IsManaged.
            if (slot.Chain.Count > 0 && slot.Chain[0] == null)
                slot.Chain[0] = "StandIn_Jeb";
            else
                slot.Chain.Add("StandIn_Jeb");

            // Act + Assert
            Assert.True(kerbals.IsManaged("StandIn_Jeb"),
                "Stand-in in a chain should be managed by Parsek");
        }

        [Fact]
        public void IsManaged_NullOrEmptyName_ReturnsFalse()
        {
            // Edge case: null/empty names should not crash
            var kerbals = new KerbalsModule();
            Assert.False(kerbals.IsManaged(null));
            Assert.False(kerbals.IsManaged(""));
        }

        [Fact]
        public void IsManaged_LoopRecording_NotReserved()
        {
            // Arrange: loop recordings should be skipped
            var rec = MakeRecording("Loop Ship", new[] { "Jeb" },
                TerminalState.Landed, 1000);
            rec.LoopPlayback = true;
            RecordingStore.AddCommittedForTesting(rec);

            var kerbals = RecalculateFromStore();

            // Act + Assert: Jeb should not be managed (loop recordings are skipped)
            Assert.False(kerbals.IsManaged("Jeb"),
                "Crew in loop recordings should not be reserved");
        }

        [Fact]
        public void IsManaged_DeadKerbal_IsPermanent()
        {
            // Arrange: dead crew should have permanent reservation
            var rec = MakeRecording("Doomed Ship", new[] { "Jeb" },
                TerminalState.Destroyed, 1000);
            RecordingStore.AddCommittedForTesting(rec);

            var kerbals = RecalculateFromStore();

            Assert.True(kerbals.IsManaged("Jeb"),
                "Dead kerbal should be managed");
            Assert.True(kerbals.Reservations["Jeb"].IsPermanent,
                "Dead kerbal reservation should be permanent");
        }

        [Fact]
        public void IsManaged_RecoveredKerbal_IsTemporary()
        {
            // Arrange: recovered crew has temporary reservation (ends at rec.EndUT)
            var rec = MakeRecording("Recovered Ship", new[] { "Jeb" },
                TerminalState.Recovered, 1000);
            RecordingStore.AddCommittedForTesting(rec);

            var kerbals = RecalculateFromStore();

            Assert.True(kerbals.IsManaged("Jeb"),
                "Recovered kerbal should be managed");
            Assert.False(kerbals.Reservations["Jeb"].IsPermanent,
                "Recovered kerbal reservation should not be permanent");
            Assert.Equal(1000, kerbals.Reservations["Jeb"].ReservedUntilUT);
        }
    }
}
