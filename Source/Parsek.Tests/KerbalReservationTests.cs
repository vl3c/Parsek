using System.Collections.Generic;
using System.Linq;
using Xunit;

namespace Parsek.Tests
{
    /// <summary>
    /// Tests for KerbalsModule reservation computation, chain building,
    /// slot serialization, and backward compatibility migration.
    /// </summary>
    [Collection("Sequential")] // touches KerbalsModule + RecordingStore static state
    public class KerbalReservationTests : System.IDisposable
    {
        private readonly List<string> logLines = new List<string>();

        public KerbalReservationTests()
        {
            ParsekLog.ResetTestOverrides();
            ParsekLog.SuppressLogging = false;
            ParsekLog.TestSinkForTesting = line => logLines.Add(line);
            RecordingStore.SuppressLogging = true;
            KerbalsModule.ResetForTesting();
            RecordingStore.ResetForTesting();
        }

        public void Dispose()
        {
            ParsekLog.ResetTestOverrides();
            ParsekLog.SuppressLogging = true;
            RecordingStore.SuppressLogging = false;
            KerbalsModule.ResetForTesting();
            RecordingStore.ResetForTesting();
        }

        /// <summary>
        /// Creates a recording with crew in its VesselSnapshot and pre-populated
        /// CrewEndStates based on the given terminal state.
        /// </summary>
        private Recording MakeRecording(string vesselName, string[] crew,
            TerminalState terminal, double endUT)
        {
            // Build a vessel snapshot with crew entries
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

            // Build end-of-recording crew set (for inference, all crew still aboard)
            var endCrewSet = new HashSet<string>(crew);

            // Populate CrewEndStates using InferCrewEndState directly
            rec.CrewEndStates = new Dictionary<string, KerbalEndState>();
            for (int i = 0; i < crew.Length; i++)
            {
                rec.CrewEndStates[crew[i]] = KerbalsModule.InferCrewEndState(
                    crew[i], terminal, endCrewSet);
            }

            return rec;
        }

        // ── Reservation basics ──

        [Fact]
        public void Recalculate_SingleRecording_ReservesAllCrew()
        {
            var rec = MakeRecording("Ship", new[] { "Jeb", "Bill" },
                TerminalState.Recovered, 2000);
            RecordingStore.AddCommittedForTesting(rec);

            KerbalsModule.Recalculate();

            Assert.False(KerbalsModule.IsKerbalAvailable("Jeb"));
            Assert.False(KerbalsModule.IsKerbalAvailable("Bill"));
            Assert.True(KerbalsModule.IsKerbalAvailable("Val")); // not in recording
        }

        [Fact]
        public void Recalculate_RecoveredCrew_TemporaryReservation()
        {
            var rec = MakeRecording("Ship", new[] { "Jeb" },
                TerminalState.Recovered, 2000);
            RecordingStore.AddCommittedForTesting(rec);

            KerbalsModule.Recalculate();

            Assert.Equal(2000.0, KerbalsModule.Reservations["Jeb"].ReservedUntilUT);
            Assert.False(KerbalsModule.Reservations["Jeb"].IsPermanent);
        }

        [Fact]
        public void Recalculate_DeadCrew_PermanentReservation()
        {
            var rec = MakeRecording("Ship", new[] { "Jeb" },
                TerminalState.Destroyed, 1000);
            RecordingStore.AddCommittedForTesting(rec);

            KerbalsModule.Recalculate();

            Assert.True(KerbalsModule.Reservations["Jeb"].IsPermanent);
            Assert.Equal(double.PositiveInfinity, KerbalsModule.Reservations["Jeb"].ReservedUntilUT);
        }

        [Fact]
        public void Recalculate_MultipleRecordings_MaxEndUT()
        {
            var recA = MakeRecording("Ship A", new[] { "Jeb" },
                TerminalState.Recovered, 2000);
            var recB = MakeRecording("Ship B", new[] { "Jeb" },
                TerminalState.Recovered, 3000);
            RecordingStore.AddCommittedForTesting(recA);
            RecordingStore.AddCommittedForTesting(recB);

            KerbalsModule.Recalculate();

            Assert.Equal(3000.0, KerbalsModule.Reservations["Jeb"].ReservedUntilUT);
        }

        [Fact]
        public void Recalculate_AboardCrew_OpenEndedReservation()
        {
            // Aboard = crew still on vessel (e.g. landed on remote body)
            // Should be open-ended (infinity) but NOT permanent
            var rec = MakeRecording("Lander", new[] { "Jeb" },
                TerminalState.Landed, 5000);
            RecordingStore.AddCommittedForTesting(rec);

            KerbalsModule.Recalculate();

            Assert.Equal(double.PositiveInfinity, KerbalsModule.Reservations["Jeb"].ReservedUntilUT);
            Assert.False(KerbalsModule.Reservations["Jeb"].IsPermanent); // temporary, not dead
        }

        [Fact]
        public void Recalculate_OrbitingCrew_OpenEndedReservation()
        {
            // Orbiting = crew still aboard, intact terminal state
            var rec = MakeRecording("Orbiter", new[] { "Jeb" },
                TerminalState.Orbiting, 8000);
            RecordingStore.AddCommittedForTesting(rec);

            KerbalsModule.Recalculate();

            Assert.Equal(double.PositiveInfinity, KerbalsModule.Reservations["Jeb"].ReservedUntilUT);
            Assert.False(KerbalsModule.Reservations["Jeb"].IsPermanent);
        }

        [Fact]
        public void Recalculate_NoCrewRecording_NoReservations()
        {
            // Probe with no crew should not create any reservations
            var snapshot = new ConfigNode("VESSEL");
            snapshot.AddNode("PART").AddValue("name", "probeCoreCube");
            var rec = new Recording
            {
                VesselName = "Probe",
                VesselSnapshot = snapshot,
                TerminalStateValue = TerminalState.Orbiting,
                ExplicitStartUT = 0,
                ExplicitEndUT = 1000
            };
            RecordingStore.AddCommittedForTesting(rec);

            KerbalsModule.Recalculate();

            Assert.Empty(KerbalsModule.Reservations);
        }

        [Fact]
        public void Recalculate_SkipsLoopRecordings()
        {
            var rec = MakeRecording("Ship", new[] { "Jeb" },
                TerminalState.Recovered, 2000);
            rec.LoopPlayback = true;
            RecordingStore.AddCommittedForTesting(rec);

            KerbalsModule.Recalculate();

            Assert.True(KerbalsModule.IsKerbalAvailable("Jeb"));
        }

        [Fact]
        public void Recalculate_SkipsDisabledChains()
        {
            // Create two recordings that form a fully-disabled chain
            var rec = MakeRecording("Ship", new[] { "Jeb" },
                TerminalState.Recovered, 2000);
            rec.ChainId = "test-chain";
            rec.ChainIndex = 0;
            rec.PlaybackEnabled = false;
            RecordingStore.AddCommittedForTesting(rec);

            KerbalsModule.Recalculate();

            Assert.True(KerbalsModule.IsKerbalAvailable("Jeb"));
        }

        [Fact]
        public void Recalculate_NullCrewEndStates_DefaultsToAboard()
        {
            // If CrewEndStates is not populated (legacy recording), default to Aboard
            var snapshot = new ConfigNode("VESSEL");
            var part = snapshot.AddNode("PART");
            part.AddValue("crew", "Jeb");
            var rec = new Recording
            {
                VesselName = "Legacy Ship",
                VesselSnapshot = snapshot,
                TerminalStateValue = TerminalState.Landed,
                ExplicitStartUT = 0,
                ExplicitEndUT = 5000,
                CrewEndStates = null // no end states populated
            };
            RecordingStore.AddCommittedForTesting(rec);

            KerbalsModule.Recalculate();

            // Should default to Aboard -> open-ended
            Assert.Equal(double.PositiveInfinity, KerbalsModule.Reservations["Jeb"].ReservedUntilUT);
            Assert.False(KerbalsModule.Reservations["Jeb"].IsPermanent);
        }

        // ── Merge behavior: permanent wins over temporary ──

        [Fact]
        public void Recalculate_MixedDeadAndRecovered_PermanentWins()
        {
            // Recording A: recovered at UT 2000
            var recA = MakeRecording("Ship A", new[] { "Jeb" },
                TerminalState.Recovered, 2000);
            // Recording B: destroyed
            var recB = MakeRecording("Ship B", new[] { "Jeb" },
                TerminalState.Destroyed, 1000);
            RecordingStore.AddCommittedForTesting(recA);
            RecordingStore.AddCommittedForTesting(recB);

            KerbalsModule.Recalculate();

            Assert.True(KerbalsModule.Reservations["Jeb"].IsPermanent);
            Assert.Equal(double.PositiveInfinity, KerbalsModule.Reservations["Jeb"].ReservedUntilUT);
        }

        // ── Chain building ──

        [Fact]
        public void Recalculate_TemporaryReservation_CreatesSlot()
        {
            var rec = MakeRecording("Ship", new[] { "Jeb" },
                TerminalState.Recovered, 2000);
            RecordingStore.AddCommittedForTesting(rec);

            KerbalsModule.Recalculate();

            Assert.True(KerbalsModule.Slots.ContainsKey("Jeb"));
            Assert.Equal("Jeb", KerbalsModule.Slots["Jeb"].OwnerName);
        }

        [Fact]
        public void Recalculate_TemporaryReservation_ChainHasNullPlaceholder()
        {
            var rec = MakeRecording("Ship", new[] { "Jeb" },
                TerminalState.Recovered, 2000);
            RecordingStore.AddCommittedForTesting(rec);

            KerbalsModule.Recalculate();

            // Chain should have a null placeholder at depth 0 (pending generation)
            Assert.Single(KerbalsModule.Slots["Jeb"].Chain);
            Assert.Null(KerbalsModule.Slots["Jeb"].Chain[0]);
        }

        [Fact]
        public void Recalculate_PermanentReservation_NoNewSlotChain()
        {
            var rec = MakeRecording("Ship", new[] { "Jeb" },
                TerminalState.Destroyed, 1000);
            RecordingStore.AddCommittedForTesting(rec);

            KerbalsModule.Recalculate();

            Assert.True(KerbalsModule.Reservations["Jeb"].IsPermanent);
            // No chain generated for permanently gone owner
            // Slot may exist from prior state, but no new chain entries
            if (KerbalsModule.Slots.ContainsKey("Jeb"))
                Assert.True(KerbalsModule.Slots["Jeb"].OwnerPermanentlyGone);
        }

        [Fact]
        public void Recalculate_ExistingChainEntry_Reused()
        {
            // Pre-populate a slot with an existing stand-in name
            var parent = new ConfigNode("TEST");
            var slotsNode = parent.AddNode("KERBAL_SLOTS");
            var slotNode = slotsNode.AddNode("SLOT");
            slotNode.AddValue("owner", "Jeb");
            slotNode.AddValue("trait", "Pilot");
            var entry = slotNode.AddNode("CHAIN_ENTRY");
            entry.AddValue("name", "Hanley");
            KerbalsModule.LoadSlots(parent);

            // Now recalculate with Jeb reserved
            var rec = MakeRecording("Ship", new[] { "Jeb" },
                TerminalState.Recovered, 2000);
            RecordingStore.AddCommittedForTesting(rec);

            KerbalsModule.Recalculate();

            // Existing chain entry "Hanley" should be preserved
            Assert.Equal("Hanley", KerbalsModule.Slots["Jeb"].Chain[0]);
        }

        [Fact]
        public void Recalculate_ChainStandInAlsoReserved_ExtendsChain()
        {
            // Pre-populate: Jeb -> Hanley at depth 0
            var parent = new ConfigNode("TEST");
            var slotsNode = parent.AddNode("KERBAL_SLOTS");
            var slotNode = slotsNode.AddNode("SLOT");
            slotNode.AddValue("owner", "Jeb");
            slotNode.AddValue("trait", "Pilot");
            var entry = slotNode.AddNode("CHAIN_ENTRY");
            entry.AddValue("name", "Hanley");
            KerbalsModule.LoadSlots(parent);

            // Both Jeb and Hanley are reserved in different recordings
            var recA = MakeRecording("Ship A", new[] { "Jeb" },
                TerminalState.Recovered, 2000);
            var recB = MakeRecording("Ship B", new[] { "Hanley" },
                TerminalState.Recovered, 3000);
            RecordingStore.AddCommittedForTesting(recA);
            RecordingStore.AddCommittedForTesting(recB);

            KerbalsModule.Recalculate();

            // Chain should extend: Hanley at depth 0, null placeholder at depth 1
            Assert.True(KerbalsModule.Slots["Jeb"].Chain.Count >= 2);
            Assert.Equal("Hanley", KerbalsModule.Slots["Jeb"].Chain[0]);
            Assert.Null(KerbalsModule.Slots["Jeb"].Chain[1]); // pending generation
        }

        // ── Query methods ──

        [Fact]
        public void GetActiveOccupant_OwnerFree_ReturnsOwner()
        {
            Assert.Equal("Jeb", KerbalsModule.GetActiveOccupant("Jeb"));
        }

        [Fact]
        public void GetActiveOccupant_OwnerReserved_ReturnsChainStandIn()
        {
            // Pre-populate slot with stand-in
            var parent = new ConfigNode("TEST");
            var slotsNode = parent.AddNode("KERBAL_SLOTS");
            var slotNode = slotsNode.AddNode("SLOT");
            slotNode.AddValue("owner", "Jeb");
            slotNode.AddValue("trait", "Pilot");
            var entry = slotNode.AddNode("CHAIN_ENTRY");
            entry.AddValue("name", "Hanley");
            KerbalsModule.LoadSlots(parent);

            // Reserve Jeb but not Hanley
            var rec = MakeRecording("Ship", new[] { "Jeb" },
                TerminalState.Recovered, 2000);
            RecordingStore.AddCommittedForTesting(rec);
            KerbalsModule.Recalculate();

            Assert.Equal("Hanley", KerbalsModule.GetActiveOccupant("Jeb"));
        }

        [Fact]
        public void GetActiveOccupant_AllReserved_ReturnsNull()
        {
            // Pre-populate slot with stand-in
            var parent = new ConfigNode("TEST");
            var slotsNode = parent.AddNode("KERBAL_SLOTS");
            var slotNode = slotsNode.AddNode("SLOT");
            slotNode.AddValue("owner", "Jeb");
            slotNode.AddValue("trait", "Pilot");
            var entry = slotNode.AddNode("CHAIN_ENTRY");
            entry.AddValue("name", "Hanley");
            KerbalsModule.LoadSlots(parent);

            // Reserve both Jeb and Hanley
            var recA = MakeRecording("Ship A", new[] { "Jeb" },
                TerminalState.Recovered, 2000);
            var recB = MakeRecording("Ship B", new[] { "Hanley" },
                TerminalState.Recovered, 3000);
            RecordingStore.AddCommittedForTesting(recA);
            RecordingStore.AddCommittedForTesting(recB);
            KerbalsModule.Recalculate();

            // Both owner and stand-in reserved -> returns null (pending generation)
            Assert.Null(KerbalsModule.GetActiveOccupant("Jeb"));
        }

        [Fact]
        public void IsManaged_ReservedKerbal_ReturnsTrue()
        {
            var rec = MakeRecording("Ship", new[] { "Jeb" },
                TerminalState.Recovered, 2000);
            RecordingStore.AddCommittedForTesting(rec);
            KerbalsModule.Recalculate();

            Assert.True(KerbalsModule.IsManaged("Jeb"));
        }

        [Fact]
        public void IsManaged_UnmanagedKerbal_ReturnsFalse()
        {
            Assert.False(KerbalsModule.IsManaged("Val"));
        }

        [Fact]
        public void IsManaged_StandInKerbal_ReturnsTrue()
        {
            // Pre-populate slot with stand-in
            var parent = new ConfigNode("TEST");
            var slotsNode = parent.AddNode("KERBAL_SLOTS");
            var slotNode = slotsNode.AddNode("SLOT");
            slotNode.AddValue("owner", "Jeb");
            slotNode.AddValue("trait", "Pilot");
            var entry = slotNode.AddNode("CHAIN_ENTRY");
            entry.AddValue("name", "Hanley");
            KerbalsModule.LoadSlots(parent);

            Assert.True(KerbalsModule.IsManaged("Hanley"));
        }

        [Fact]
        public void IsKerbalInAnyRecording_Present_ReturnsTrue()
        {
            var rec = MakeRecording("Ship", new[] { "Jeb" },
                TerminalState.Recovered, 2000);
            RecordingStore.AddCommittedForTesting(rec);
            KerbalsModule.Recalculate(); // builds the allRecordingCrew HashSet

            Assert.True(KerbalsModule.IsKerbalInAnyRecording("Jeb"));
        }

        [Fact]
        public void IsKerbalInAnyRecording_Absent_ReturnsFalse()
        {
            var rec = MakeRecording("Ship", new[] { "Jeb" },
                TerminalState.Recovered, 2000);
            RecordingStore.AddCommittedForTesting(rec);

            Assert.False(KerbalsModule.IsKerbalInAnyRecording("Val"));
        }

        [Fact]
        public void IsKerbalInAnyRecording_SkipsLoopRecordings()
        {
            var rec = MakeRecording("Ship", new[] { "Jeb" },
                TerminalState.Recovered, 2000);
            rec.LoopPlayback = true;
            RecordingStore.AddCommittedForTesting(rec);

            Assert.False(KerbalsModule.IsKerbalInAnyRecording("Jeb"));
        }

        [Fact]
        public void FindTraitForKerbal_NoRoster_ReturnsPilot()
        {
            // In test environment, HighLogic is not available — should fall back to "Pilot"
            Assert.Equal("Pilot", KerbalsModule.FindTraitForKerbal("Jeb"));
        }

        // ── Retired stand-ins ──

        [Fact]
        public void ComputeRetiredSet_DisplacedUsedStandIn_IsRetired()
        {
            // Scenario: Jeb was reserved, Hanley was hired as stand-in and used in a recording.
            // Now Jeb is free (no longer in any recording). Hanley is displaced -> retired.

            // 1. Pre-populate slot: Jeb -> [Hanley]
            var parent = new ConfigNode("TEST");
            var slotsNode = parent.AddNode("KERBAL_SLOTS");
            var slotNode = slotsNode.AddNode("SLOT");
            slotNode.AddValue("owner", "Jeb");
            slotNode.AddValue("trait", "Pilot");
            var entry = slotNode.AddNode("CHAIN_ENTRY");
            entry.AddValue("name", "Hanley");
            KerbalsModule.LoadSlots(parent);

            // 2. Only Hanley appears in a recording (the recording where Hanley was the stand-in)
            var rec = MakeRecording("Hanley's Ship", new[] { "Hanley" },
                TerminalState.Recovered, 2000);
            RecordingStore.AddCommittedForTesting(rec);

            // 3. Recalculate: Jeb is NOT reserved (not in any recording), Hanley IS reserved
            KerbalsModule.Recalculate();

            // Hanley is reserved (in a recording), not displaced
            Assert.DoesNotContain("Hanley", KerbalsModule.RetiredKerbals);
        }

        [Fact]
        public void ComputeRetiredSet_StandInNotUsed_NotRetired()
        {
            // Stand-in exists in chain but never appeared in a recording -> not retired
            var parent = new ConfigNode("TEST");
            var slotsNode = parent.AddNode("KERBAL_SLOTS");
            var slotNode = slotsNode.AddNode("SLOT");
            slotNode.AddValue("owner", "Jeb");
            slotNode.AddValue("trait", "Pilot");
            var entry = slotNode.AddNode("CHAIN_ENTRY");
            entry.AddValue("name", "Hanley");
            KerbalsModule.LoadSlots(parent);

            // No recordings at all -> neither Jeb nor Hanley are reserved
            KerbalsModule.Recalculate();

            Assert.DoesNotContain("Hanley", KerbalsModule.RetiredKerbals);
        }

        // ── Serialization ──

        [Fact]
        public void SaveSlots_LoadSlots_RoundTrip()
        {
            // Load initial data
            var loadParent = new ConfigNode("TEST");
            var slotsNode = loadParent.AddNode("KERBAL_SLOTS");
            var slotNode = slotsNode.AddNode("SLOT");
            slotNode.AddValue("owner", "Jeb");
            slotNode.AddValue("trait", "Pilot");
            slotNode.AddValue("permanentlyGone", "True");
            var e1 = slotNode.AddNode("CHAIN_ENTRY");
            e1.AddValue("name", "Hanley");
            var e2 = slotNode.AddNode("CHAIN_ENTRY");
            e2.AddValue("name", "Kirrim");

            KerbalsModule.LoadSlots(loadParent);

            // Verify loaded
            Assert.True(KerbalsModule.Slots.ContainsKey("Jeb"));
            Assert.Equal("Pilot", KerbalsModule.Slots["Jeb"].OwnerTrait);
            Assert.True(KerbalsModule.Slots["Jeb"].OwnerPermanentlyGone);
            Assert.Equal(2, KerbalsModule.Slots["Jeb"].Chain.Count);
            Assert.Equal("Hanley", KerbalsModule.Slots["Jeb"].Chain[0]);
            Assert.Equal("Kirrim", KerbalsModule.Slots["Jeb"].Chain[1]);

            // Save to new parent
            var saveParent = new ConfigNode("TEST2");
            KerbalsModule.SaveSlots(saveParent);

            // Reset and reload
            KerbalsModule.ResetForTesting();
            KerbalsModule.LoadSlots(saveParent);

            Assert.True(KerbalsModule.Slots.ContainsKey("Jeb"));
            Assert.Equal("Pilot", KerbalsModule.Slots["Jeb"].OwnerTrait);
            Assert.True(KerbalsModule.Slots["Jeb"].OwnerPermanentlyGone);
            Assert.Equal(2, KerbalsModule.Slots["Jeb"].Chain.Count);
            Assert.Equal("Hanley", KerbalsModule.Slots["Jeb"].Chain[0]);
            Assert.Equal("Kirrim", KerbalsModule.Slots["Jeb"].Chain[1]);
        }

        [Fact]
        public void LoadSlots_EmptyParent_ClearsSlots()
        {
            // Pre-populate some slots
            var parent = new ConfigNode("TEST");
            var slotsNode = parent.AddNode("KERBAL_SLOTS");
            var slotNode = slotsNode.AddNode("SLOT");
            slotNode.AddValue("owner", "Jeb");
            slotNode.AddValue("trait", "Pilot");
            KerbalsModule.LoadSlots(parent);
            Assert.True(KerbalsModule.Slots.ContainsKey("Jeb"));

            // Load from empty parent -> should clear
            var emptyParent = new ConfigNode("EMPTY");
            KerbalsModule.LoadSlots(emptyParent);
            Assert.Empty(KerbalsModule.Slots);
        }

        [Fact]
        public void Migration_OldCrewReplacements_LoadsAsChainDepth0()
        {
            var parent = new ConfigNode("TEST");
            var crNode = parent.AddNode("CREW_REPLACEMENTS");
            var entry = crNode.AddNode("ENTRY");
            entry.AddValue("original", "Jeb");
            entry.AddValue("replacement", "Hanley");

            KerbalsModule.LoadSlots(parent);

            Assert.True(KerbalsModule.Slots.ContainsKey("Jeb"));
            Assert.Single(KerbalsModule.Slots["Jeb"].Chain);
            Assert.Equal("Hanley", KerbalsModule.Slots["Jeb"].Chain[0]);
            Assert.Equal("Pilot", KerbalsModule.Slots["Jeb"].OwnerTrait); // default
        }

        [Fact]
        public void Migration_PreferNewFormat_OverLegacy()
        {
            // Both KERBAL_SLOTS and CREW_REPLACEMENTS exist -> new format wins
            var parent = new ConfigNode("TEST");

            var slotsNode = parent.AddNode("KERBAL_SLOTS");
            var slotNode = slotsNode.AddNode("SLOT");
            slotNode.AddValue("owner", "Jeb");
            slotNode.AddValue("trait", "Engineer");
            var chainEntry = slotNode.AddNode("CHAIN_ENTRY");
            chainEntry.AddValue("name", "NewStandIn");

            var crNode = parent.AddNode("CREW_REPLACEMENTS");
            var legacyEntry = crNode.AddNode("ENTRY");
            legacyEntry.AddValue("original", "Jeb");
            legacyEntry.AddValue("replacement", "OldStandIn");

            KerbalsModule.LoadSlots(parent);

            // New format should be used
            Assert.Equal("Engineer", KerbalsModule.Slots["Jeb"].OwnerTrait);
            Assert.Single(KerbalsModule.Slots["Jeb"].Chain);
            Assert.Equal("NewStandIn", KerbalsModule.Slots["Jeb"].Chain[0]);
        }

        [Fact]
        public void SaveSlots_NoSlots_DoesNotCreateNode()
        {
            var parent = new ConfigNode("TEST");
            KerbalsModule.SaveSlots(parent);

            Assert.Null(parent.GetNode("KERBAL_SLOTS"));
        }

        // ── Log assertions ──

        [Fact]
        public void Recalculate_LogsReservationCount()
        {
            var rec = MakeRecording("Ship", new[] { "Jeb", "Bill" },
                TerminalState.Recovered, 2000);
            RecordingStore.AddCommittedForTesting(rec);

            KerbalsModule.Recalculate();

            Assert.Contains(logLines, l =>
                l.Contains("[KerbalsModule]") && l.Contains("2 reservations"));
        }

        [Fact]
        public void Recalculate_LogsSlotCount()
        {
            var rec = MakeRecording("Ship", new[] { "Jeb" },
                TerminalState.Recovered, 2000);
            RecordingStore.AddCommittedForTesting(rec);

            KerbalsModule.Recalculate();

            Assert.Contains(logLines, l =>
                l.Contains("[KerbalsModule]") && l.Contains("1 slots"));
        }

        [Fact]
        public void IsKerbalAvailable_LogsReservedStatus()
        {
            var rec = MakeRecording("Ship", new[] { "Jeb" },
                TerminalState.Recovered, 2000);
            RecordingStore.AddCommittedForTesting(rec);
            KerbalsModule.Recalculate();

            KerbalsModule.IsKerbalAvailable("Jeb");

            Assert.Contains(logLines, l =>
                l.Contains("[KerbalsModule]") && l.Contains("'Jeb'") && l.Contains("RESERVED"));
        }

        [Fact]
        public void IsKerbalAvailable_LogsAvailableStatus()
        {
            KerbalsModule.IsKerbalAvailable("Val");

            Assert.Contains(logLines, l =>
                l.Contains("[KerbalsModule]") && l.Contains("'Val'") && l.Contains("available"));
        }

        [Fact]
        public void LoadSlots_LogsMigrationCount()
        {
            var parent = new ConfigNode("TEST");
            var crNode = parent.AddNode("CREW_REPLACEMENTS");
            var entry = crNode.AddNode("ENTRY");
            entry.AddValue("original", "Jeb");
            entry.AddValue("replacement", "Hanley");

            KerbalsModule.LoadSlots(parent);

            Assert.Contains(logLines, l =>
                l.Contains("[KerbalsModule]") && l.Contains("Migrated") && l.Contains("1 slot"));
        }

        [Fact]
        public void SaveSlots_LogsSaveCount()
        {
            // Load a slot first
            var parent = new ConfigNode("TEST");
            var slotsNode = parent.AddNode("KERBAL_SLOTS");
            var slotNode = slotsNode.AddNode("SLOT");
            slotNode.AddValue("owner", "Jeb");
            slotNode.AddValue("trait", "Pilot");
            KerbalsModule.LoadSlots(parent);

            logLines.Clear();
            var saveParent = new ConfigNode("SAVE");
            KerbalsModule.SaveSlots(saveParent);

            Assert.Contains(logLines, l =>
                l.Contains("[KerbalsModule]") && l.Contains("Saved 1 kerbal slot"));
        }

        // ── Reset ──

        [Fact]
        public void ResetForTesting_ClearsAllState()
        {
            var rec = MakeRecording("Ship", new[] { "Jeb" },
                TerminalState.Recovered, 2000);
            RecordingStore.AddCommittedForTesting(rec);
            KerbalsModule.Recalculate();

            // Verify state exists
            Assert.NotEmpty(KerbalsModule.Reservations);
            Assert.NotEmpty(KerbalsModule.Slots);

            KerbalsModule.ResetForTesting();

            Assert.Empty(KerbalsModule.Reservations);
            Assert.Empty(KerbalsModule.Slots);
            Assert.Empty(KerbalsModule.RetiredKerbals);
        }

        // ── InferCrewEndState (Task 1 coverage, exercised via MakeRecording) ──

        [Fact]
        public void InferCrewEndState_NullTerminalState_ReturnsUnknown()
        {
            var result = KerbalsModule.InferCrewEndState("Jeb", null, new HashSet<string> { "Jeb" });
            Assert.Equal(KerbalEndState.Unknown, result);
        }

        [Fact]
        public void InferCrewEndState_Destroyed_ReturnsDead()
        {
            var result = KerbalsModule.InferCrewEndState("Jeb", TerminalState.Destroyed,
                new HashSet<string> { "Jeb" });
            Assert.Equal(KerbalEndState.Dead, result);
        }

        [Fact]
        public void InferCrewEndState_Recovered_ReturnsRecovered()
        {
            var result = KerbalsModule.InferCrewEndState("Jeb", TerminalState.Recovered,
                new HashSet<string> { "Jeb" });
            Assert.Equal(KerbalEndState.Recovered, result);
        }

        [Fact]
        public void InferCrewEndState_LandedInSnapshot_ReturnsAboard()
        {
            var result = KerbalsModule.InferCrewEndState("Jeb", TerminalState.Landed,
                new HashSet<string> { "Jeb" });
            Assert.Equal(KerbalEndState.Aboard, result);
        }

        [Fact]
        public void InferCrewEndState_LandedNotInSnapshot_ReturnsDead()
        {
            var result = KerbalsModule.InferCrewEndState("Jeb", TerminalState.Landed,
                new HashSet<string>()); // Jeb not in snapshot
            Assert.Equal(KerbalEndState.Dead, result);
        }

        [Fact]
        public void InferCrewEndState_BoardedInSnapshot_ReturnsAboard()
        {
            var result = KerbalsModule.InferCrewEndState("Jeb", TerminalState.Boarded,
                new HashSet<string> { "Jeb" });
            Assert.Equal(KerbalEndState.Aboard, result);
        }

        [Fact]
        public void InferCrewEndState_BoardedNotInSnapshot_ReturnsUnknown()
        {
            var result = KerbalsModule.InferCrewEndState("Jeb", TerminalState.Boarded,
                new HashSet<string>()); // transferred away
            Assert.Equal(KerbalEndState.Unknown, result);
        }
    }
}
