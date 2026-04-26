using System.Collections.Generic;
using System.Linq;
using System.Reflection;
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
            RecordingStore.ResetForTesting();
            GameStateStore.SuppressLogging = true;
            GameStateStore.ResetForTesting();
        }

        public void Dispose()
        {
            ParsekLog.ResetTestOverrides();
            ParsekLog.SuppressLogging = true;
            RecordingStore.SuppressLogging = false;
            RecordingStore.ResetForTesting();
            GameStateStore.ResetForTesting();
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
            RecordingStore.AddRecordingWithTreeForTesting(rec);

            var kerbals = KerbalsTestHelper.RecalculateFromStore();

            Assert.False(kerbals.IsKerbalAvailable("Jeb"));
            Assert.False(kerbals.IsKerbalAvailable("Bill"));
            Assert.True(kerbals.IsKerbalAvailable("Val")); // not in recording
        }

        [Fact]
        public void Recalculate_RecoveredCrew_TemporaryReservation()
        {
            var rec = MakeRecording("Ship", new[] { "Jeb" },
                TerminalState.Recovered, 2000);
            RecordingStore.AddRecordingWithTreeForTesting(rec);

            var kerbals = KerbalsTestHelper.RecalculateFromStore();

            Assert.Equal(2000.0, kerbals.Reservations["Jeb"].ReservedUntilUT);
            Assert.False(kerbals.Reservations["Jeb"].IsPermanent);
        }

        [Fact]
        public void Recalculate_DeadCrew_PermanentReservation()
        {
            var rec = MakeRecording("Ship", new[] { "Jeb" },
                TerminalState.Destroyed, 1000);
            RecordingStore.AddRecordingWithTreeForTesting(rec);

            var kerbals = KerbalsTestHelper.RecalculateFromStore();

            Assert.True(kerbals.Reservations["Jeb"].IsPermanent);
            Assert.Equal(double.PositiveInfinity, kerbals.Reservations["Jeb"].ReservedUntilUT);
        }

        [Fact]
        public void Recalculate_MultipleRecordings_MaxEndUT()
        {
            var recA = MakeRecording("Ship A", new[] { "Jeb" },
                TerminalState.Recovered, 2000);
            var recB = MakeRecording("Ship B", new[] { "Jeb" },
                TerminalState.Recovered, 3000);
            RecordingStore.AddRecordingWithTreeForTesting(recA);
            RecordingStore.AddRecordingWithTreeForTesting(recB);

            var kerbals = KerbalsTestHelper.RecalculateFromStore();

            Assert.Equal(3000.0, kerbals.Reservations["Jeb"].ReservedUntilUT);
        }

        [Fact]
        public void Recalculate_AboardCrew_OpenEndedReservation()
        {
            // Aboard = crew still on vessel (e.g. landed on remote body)
            // Should be open-ended (infinity) but NOT permanent
            var rec = MakeRecording("Lander", new[] { "Jeb" },
                TerminalState.Landed, 5000);
            RecordingStore.AddRecordingWithTreeForTesting(rec);

            var kerbals = KerbalsTestHelper.RecalculateFromStore();

            Assert.Equal(double.PositiveInfinity, kerbals.Reservations["Jeb"].ReservedUntilUT);
            Assert.False(kerbals.Reservations["Jeb"].IsPermanent); // temporary, not dead
        }

        [Fact]
        public void Recalculate_OrbitingCrew_OpenEndedReservation()
        {
            // Orbiting = crew still aboard, intact terminal state
            var rec = MakeRecording("Orbiter", new[] { "Jeb" },
                TerminalState.Orbiting, 8000);
            RecordingStore.AddRecordingWithTreeForTesting(rec);

            var kerbals = KerbalsTestHelper.RecalculateFromStore();

            Assert.Equal(double.PositiveInfinity, kerbals.Reservations["Jeb"].ReservedUntilUT);
            Assert.False(kerbals.Reservations["Jeb"].IsPermanent);
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
            RecordingStore.AddRecordingWithTreeForTesting(rec);

            var kerbals = KerbalsTestHelper.RecalculateFromStore();

            Assert.Empty(kerbals.Reservations);
        }

        [Fact]
        public void Recalculate_SkipsLoopRecordings()
        {
            var rec = MakeRecording("Ship", new[] { "Jeb" },
                TerminalState.Recovered, 2000);
            rec.LoopPlayback = true;
            RecordingStore.AddRecordingWithTreeForTesting(rec);

            var kerbals = KerbalsTestHelper.RecalculateFromStore();

            Assert.True(kerbals.IsKerbalAvailable("Jeb"));
        }

        [Fact]
        public void Recalculate_DisabledChain_StillReservesCrew()
        {
            // Bug #433: disabling a chain's playback visual must not release its crew
            // back to the roster. Reservations follow the committed ledger, not the
            // visibility toggle.
            var rec = MakeRecording("Ship", new[] { "Jeb" },
                TerminalState.Recovered, 2000);
            rec.ChainId = "test-chain";
            rec.ChainIndex = 0;
            rec.PlaybackEnabled = false;
            RecordingStore.AddRecordingWithTreeForTesting(rec);

            var kerbals = KerbalsTestHelper.RecalculateFromStore();

            Assert.False(kerbals.IsKerbalAvailable("Jeb"));
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
            RecordingStore.AddRecordingWithTreeForTesting(rec);

            var kerbals = KerbalsTestHelper.RecalculateFromStore();

            // Should default to Aboard -> open-ended
            Assert.Equal(double.PositiveInfinity, kerbals.Reservations["Jeb"].ReservedUntilUT);
            Assert.False(kerbals.Reservations["Jeb"].IsPermanent);
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
            RecordingStore.AddRecordingWithTreeForTesting(recA);
            RecordingStore.AddRecordingWithTreeForTesting(recB);

            var kerbals = KerbalsTestHelper.RecalculateFromStore();

            Assert.True(kerbals.Reservations["Jeb"].IsPermanent);
            Assert.Equal(double.PositiveInfinity, kerbals.Reservations["Jeb"].ReservedUntilUT);
        }

        // ── Chain building ──

        [Fact]
        public void Recalculate_TemporaryReservation_CreatesSlot()
        {
            var rec = MakeRecording("Ship", new[] { "Jeb" },
                TerminalState.Recovered, 2000);
            RecordingStore.AddRecordingWithTreeForTesting(rec);

            var kerbals = KerbalsTestHelper.RecalculateFromStore();

            Assert.True(kerbals.Slots.ContainsKey("Jeb"));
            Assert.Equal("Jeb", kerbals.Slots["Jeb"].OwnerName);
        }

        [Fact]
        public void Recalculate_TemporaryReservation_ChainHasNullPlaceholder()
        {
            var rec = MakeRecording("Ship", new[] { "Jeb" },
                TerminalState.Recovered, 2000);
            RecordingStore.AddRecordingWithTreeForTesting(rec);

            var kerbals = KerbalsTestHelper.RecalculateFromStore();

            // Chain should have a null placeholder at depth 0 (pending generation)
            Assert.Single(kerbals.Slots["Jeb"].Chain);
            Assert.Null(kerbals.Slots["Jeb"].Chain[0]);
        }

        [Fact]
        public void Recalculate_PermanentReservation_NoNewSlotChain()
        {
            var rec = MakeRecording("Ship", new[] { "Jeb" },
                TerminalState.Destroyed, 1000);
            RecordingStore.AddRecordingWithTreeForTesting(rec);

            var kerbals = KerbalsTestHelper.RecalculateFromStore();

            Assert.True(kerbals.Reservations["Jeb"].IsPermanent);
            // No chain generated for permanently gone owner
            // Slot may exist from prior state, but no new chain entries
            if (kerbals.Slots.ContainsKey("Jeb"))
                Assert.True(kerbals.Slots["Jeb"].OwnerPermanentlyGone);
        }

        [Fact]
        public void Recalculate_PermanentReservation_WithExistingChain_HasNoActiveStandIn()
        {
            var module = new KerbalsModule();
            var parent = new ConfigNode("TEST");
            var slotsNode = parent.AddNode("KERBAL_SLOTS");
            var slotNode = slotsNode.AddNode("SLOT");
            slotNode.AddValue("owner", "Jeb");
            slotNode.AddValue("trait", "Pilot");
            var entry = slotNode.AddNode("CHAIN_ENTRY");
            entry.AddValue("name", "Hanley");
            module.LoadSlots(parent);

            var rec = MakeRecording("Ship", new[] { "Jeb" },
                TerminalState.Destroyed, 1000);
            RecordingStore.AddRecordingWithTreeForTesting(rec);

            var kerbals = KerbalsTestHelper.RecalculateModule(module);

            Assert.True(kerbals.Slots["Jeb"].OwnerPermanentlyGone);
            Assert.Null(kerbals.GetActiveOccupant("Jeb"));
        }

        [Fact]
        public void Recalculate_RemovedPermanentReservation_ClearsOwnerPermanentlyGone()
        {
            var module = new KerbalsModule();
            var parent = new ConfigNode("TEST");
            var slotsNode = parent.AddNode("KERBAL_SLOTS");
            var slotNode = slotsNode.AddNode("SLOT");
            slotNode.AddValue("owner", "Jeb");
            slotNode.AddValue("trait", "Pilot");
            slotNode.AddValue("permanentlyGone", "True");
            var entry = slotNode.AddNode("CHAIN_ENTRY");
            entry.AddValue("name", "Hanley");
            module.LoadSlots(parent);

            var kerbals = KerbalsTestHelper.RecalculateModule(module);

            Assert.False(kerbals.Slots["Jeb"].OwnerPermanentlyGone);
            Assert.Equal("Jeb", kerbals.GetActiveOccupant("Jeb"));
        }

        [Fact]
        public void Recalculate_ExistingChainEntry_Reused()
        {
            // Pre-populate a slot with an existing stand-in name
            var module = new KerbalsModule();
            var parent = new ConfigNode("TEST");
            var slotsNode = parent.AddNode("KERBAL_SLOTS");
            var slotNode = slotsNode.AddNode("SLOT");
            slotNode.AddValue("owner", "Jeb");
            slotNode.AddValue("trait", "Pilot");
            var entry = slotNode.AddNode("CHAIN_ENTRY");
            entry.AddValue("name", "Hanley");
            module.LoadSlots(parent);

            // Now recalculate with Jeb reserved
            var rec = MakeRecording("Ship", new[] { "Jeb" },
                TerminalState.Recovered, 2000);
            RecordingStore.AddRecordingWithTreeForTesting(rec);

            var kerbals = KerbalsTestHelper.RecalculateModule(module);

            // Existing chain entry "Hanley" should be preserved
            Assert.Equal("Hanley", kerbals.Slots["Jeb"].Chain[0]);
        }

        [Fact]
        public void Recalculate_ChainStandInAlsoReserved_ExtendsChain()
        {
            // Pre-populate: Jeb -> Hanley at depth 0
            var module = new KerbalsModule();
            var parent = new ConfigNode("TEST");
            var slotsNode = parent.AddNode("KERBAL_SLOTS");
            var slotNode = slotsNode.AddNode("SLOT");
            slotNode.AddValue("owner", "Jeb");
            slotNode.AddValue("trait", "Pilot");
            var entry = slotNode.AddNode("CHAIN_ENTRY");
            entry.AddValue("name", "Hanley");
            module.LoadSlots(parent);

            // Both Jeb and Hanley are reserved in different recordings
            var recA = MakeRecording("Ship A", new[] { "Jeb" },
                TerminalState.Recovered, 2000);
            var recB = MakeRecording("Ship B", new[] { "Hanley" },
                TerminalState.Recovered, 3000);
            RecordingStore.AddRecordingWithTreeForTesting(recA);
            RecordingStore.AddRecordingWithTreeForTesting(recB);

            var kerbals = KerbalsTestHelper.RecalculateModule(module);

            // Chain should extend: Hanley at depth 0, null placeholder at depth 1
            Assert.True(kerbals.Slots["Jeb"].Chain.Count >= 2);
            Assert.Equal("Hanley", kerbals.Slots["Jeb"].Chain[0]);
            Assert.Null(kerbals.Slots["Jeb"].Chain[1]); // pending generation
        }

        // ── Query methods ──

        [Fact]
        public void GetActiveOccupant_OwnerFree_ReturnsOwner()
        {
            var kerbals = new KerbalsModule();
            Assert.Equal("Jeb", kerbals.GetActiveOccupant("Jeb"));
        }

        [Fact]
        public void GetActiveOccupant_OwnerReserved_ReturnsChainStandIn()
        {
            // Pre-populate slot with stand-in
            var module = new KerbalsModule();
            var parent = new ConfigNode("TEST");
            var slotsNode = parent.AddNode("KERBAL_SLOTS");
            var slotNode = slotsNode.AddNode("SLOT");
            slotNode.AddValue("owner", "Jeb");
            slotNode.AddValue("trait", "Pilot");
            var entry = slotNode.AddNode("CHAIN_ENTRY");
            entry.AddValue("name", "Hanley");
            module.LoadSlots(parent);

            // Reserve Jeb but not Hanley
            var rec = MakeRecording("Ship", new[] { "Jeb" },
                TerminalState.Recovered, 2000);
            RecordingStore.AddRecordingWithTreeForTesting(rec);
            var kerbals = KerbalsTestHelper.RecalculateModule(module);

            Assert.Equal("Hanley", kerbals.GetActiveOccupant("Jeb"));
        }

        [Fact]
        public void GetActiveOccupant_FirstFreeStandInReclaimsFromDeeperFreeStandIn()
        {
            var module = new KerbalsModule();
            var parent = new ConfigNode("TEST");
            var slotsNode = parent.AddNode("KERBAL_SLOTS");
            var slotNode = slotsNode.AddNode("SLOT");
            slotNode.AddValue("owner", "Jeb");
            slotNode.AddValue("trait", "Pilot");
            var first = slotNode.AddNode("CHAIN_ENTRY");
            first.AddValue("name", "Hanley");
            var second = slotNode.AddNode("CHAIN_ENTRY");
            second.AddValue("name", "Kirrim");
            module.LoadSlots(parent);

            var rec = MakeRecording("Ship", new[] { "Jeb" },
                TerminalState.Recovered, 2000);
            RecordingStore.AddRecordingWithTreeForTesting(rec);

            var kerbals = KerbalsTestHelper.RecalculateModule(module);

            Assert.Equal("Hanley", kerbals.GetActiveOccupant("Jeb"));
        }

        [Fact]
        public void GetActiveOccupant_AllReserved_ReturnsNull()
        {
            // Pre-populate slot with stand-in
            var module = new KerbalsModule();
            var parent = new ConfigNode("TEST");
            var slotsNode = parent.AddNode("KERBAL_SLOTS");
            var slotNode = slotsNode.AddNode("SLOT");
            slotNode.AddValue("owner", "Jeb");
            slotNode.AddValue("trait", "Pilot");
            var entry = slotNode.AddNode("CHAIN_ENTRY");
            entry.AddValue("name", "Hanley");
            module.LoadSlots(parent);

            // Reserve both Jeb and Hanley
            var recA = MakeRecording("Ship A", new[] { "Jeb" },
                TerminalState.Recovered, 2000);
            var recB = MakeRecording("Ship B", new[] { "Hanley" },
                TerminalState.Recovered, 3000);
            RecordingStore.AddRecordingWithTreeForTesting(recA);
            RecordingStore.AddRecordingWithTreeForTesting(recB);
            var kerbals = KerbalsTestHelper.RecalculateModule(module);

            // Both owner and stand-in reserved -> returns null (pending generation)
            Assert.Null(kerbals.GetActiveOccupant("Jeb"));
        }

        [Fact]
        public void IsManaged_ReservedKerbal_ReturnsTrue()
        {
            var rec = MakeRecording("Ship", new[] { "Jeb" },
                TerminalState.Recovered, 2000);
            RecordingStore.AddRecordingWithTreeForTesting(rec);
            var kerbals = KerbalsTestHelper.RecalculateFromStore();

            Assert.True(kerbals.IsManaged("Jeb"));
        }

        [Fact]
        public void IsManaged_UnmanagedKerbal_ReturnsFalse()
        {
            var kerbals = new KerbalsModule();
            Assert.False(kerbals.IsManaged("Val"));
        }

        [Fact]
        public void IsManaged_StandInKerbal_ReturnsTrue()
        {
            // Pre-populate slot with stand-in
            var kerbals = new KerbalsModule();
            var parent = new ConfigNode("TEST");
            var slotsNode = parent.AddNode("KERBAL_SLOTS");
            var slotNode = slotsNode.AddNode("SLOT");
            slotNode.AddValue("owner", "Jeb");
            slotNode.AddValue("trait", "Pilot");
            var entry = slotNode.AddNode("CHAIN_ENTRY");
            entry.AddValue("name", "Hanley");
            kerbals.LoadSlots(parent);

            Assert.True(kerbals.IsManaged("Hanley"));
        }

        [Fact]
        public void IsKerbalInAnyRecording_Present_ReturnsTrue()
        {
            var rec = MakeRecording("Ship", new[] { "Jeb" },
                TerminalState.Recovered, 2000);
            RecordingStore.AddRecordingWithTreeForTesting(rec);
            var kerbals = KerbalsTestHelper.RecalculateFromStore(); // builds the allRecordingCrew HashSet

            Assert.True(kerbals.IsKerbalInAnyRecording("Jeb"));
        }

        [Fact]
        public void IsKerbalInAnyRecording_Absent_ReturnsFalse()
        {
            var rec = MakeRecording("Ship", new[] { "Jeb" },
                TerminalState.Recovered, 2000);
            RecordingStore.AddRecordingWithTreeForTesting(rec);
            var kerbals = KerbalsTestHelper.RecalculateFromStore();

            Assert.False(kerbals.IsKerbalInAnyRecording("Val"));
        }

        [Fact]
        public void IsKerbalInAnyRecording_SkipsLoopRecordings()
        {
            var rec = MakeRecording("Ship", new[] { "Jeb" },
                TerminalState.Recovered, 2000);
            rec.LoopPlayback = true;
            RecordingStore.AddRecordingWithTreeForTesting(rec);
            var kerbals = KerbalsTestHelper.RecalculateFromStore();

            Assert.False(kerbals.IsKerbalInAnyRecording("Jeb"));
        }

        [Fact]
        public void FindTraitForKerbal_NoRoster_ReturnsPilot()
        {
            // In test environment, HighLogic is not available — should fall back to "Pilot"
            Assert.Equal("Pilot", KerbalsModule.FindTraitForKerbal("Jeb"));
        }

        [Fact]
        public void FindTraitForKerbal_FallsBackToLatestBaselineTrait()
        {
            var baseline = new GameStateBaseline();
            baseline.crewEntries.Add(new GameStateBaseline.CrewEntry
            {
                name = "Tourist Kerman",
                trait = "Tourist"
            });
            GameStateStore.AddBaseline(baseline);

            Assert.Equal("Tourist", KerbalsModule.FindTraitForKerbal("Tourist Kerman"));
        }

        [Fact]
        public void ProcessAction_TouristKerbalAssignment_IsIgnored()
        {
            var module = new KerbalsModule();
            var rec = MakeRecording("Tour Bus", new[] { "Tourist Kerman" },
                TerminalState.Recovered, 1000);
            rec.RecordingId = "rec-tourist-action";
            RecordingStore.AddRecordingWithTreeForTesting(rec);

            var action = new GameAction
            {
                UT = 0,
                Type = GameActionType.KerbalAssignment,
                RecordingId = "rec-tourist-action",
                KerbalName = "Tourist Kerman",
                KerbalRole = "Tourist",
                KerbalEndStateField = KerbalEndState.Recovered,
                StartUT = 0,
                EndUT = 1000,
                Sequence = 1
            };

            module.PrePass(new List<GameAction> { action });
            module.ProcessAction(action);

            Assert.Empty(module.Reservations);
        }

        // ── Retired stand-ins ──

        [Fact]
        public void ComputeRetiredSet_DisplacedUsedStandIn_IsRetired()
        {
            // Scenario: Jeb was reserved, Hanley was hired as stand-in and used in a recording.
            // Now Jeb is free (no longer in any recording). Hanley is displaced -> retired.

            // 1. Pre-populate slot: Jeb -> [Hanley]
            var module = new KerbalsModule();
            var parent = new ConfigNode("TEST");
            var slotsNode = parent.AddNode("KERBAL_SLOTS");
            var slotNode = slotsNode.AddNode("SLOT");
            slotNode.AddValue("owner", "Jeb");
            slotNode.AddValue("trait", "Pilot");
            var entry = slotNode.AddNode("CHAIN_ENTRY");
            entry.AddValue("name", "Hanley");
            module.LoadSlots(parent);

            // 2. Only Hanley appears in a recording (the recording where Hanley was the stand-in)
            var rec = MakeRecording("Hanley's Ship", new[] { "Hanley" },
                TerminalState.Recovered, 2000);
            RecordingStore.AddRecordingWithTreeForTesting(rec);

            // 3. Recalculate: Jeb is NOT reserved (not in any recording), Hanley IS reserved
            var kerbals = KerbalsTestHelper.RecalculateModule(module);

            // Hanley is reserved (in a recording), not displaced
            Assert.DoesNotContain("Hanley", kerbals.RetiredKerbals);
        }

        [Fact]
        public void ComputeRetiredSet_StandInNotUsed_NotRetired()
        {
            // Stand-in exists in chain but never appeared in a recording -> not retired
            var module = new KerbalsModule();
            var parent = new ConfigNode("TEST");
            var slotsNode = parent.AddNode("KERBAL_SLOTS");
            var slotNode = slotsNode.AddNode("SLOT");
            slotNode.AddValue("owner", "Jeb");
            slotNode.AddValue("trait", "Pilot");
            var entry = slotNode.AddNode("CHAIN_ENTRY");
            entry.AddValue("name", "Hanley");
            module.LoadSlots(parent);

            // No recordings at all -> neither Jeb nor Hanley are reserved
            var kerbals = KerbalsTestHelper.RecalculateModule(module);

            Assert.DoesNotContain("Hanley", kerbals.RetiredKerbals);
        }

        [Fact]
        public void ComputeRetiredSet_FreeIntermediateDisplacesUsedDeeperStandIn()
        {
            var module = new KerbalsModule();
            var parent = new ConfigNode("TEST");
            var slotsNode = parent.AddNode("KERBAL_SLOTS");
            var slotNode = slotsNode.AddNode("SLOT");
            slotNode.AddValue("owner", "Jeb");
            slotNode.AddValue("trait", "Pilot");
            var first = slotNode.AddNode("CHAIN_ENTRY");
            first.AddValue("name", "Hanley");
            var second = slotNode.AddNode("CHAIN_ENTRY");
            second.AddValue("name", "Kirrim");
            module.LoadSlots(parent);

            var rec = MakeRecording("Ship", new[] { "Jeb" },
                TerminalState.Recovered, 1000);
            rec.RecordingId = "rec-jeb";
            RecordingStore.AddRecordingWithTreeForTesting(rec);

            var actions = new List<GameAction>
            {
                new GameAction
                {
                    UT = 0,
                    Type = GameActionType.KerbalAssignment,
                    RecordingId = "rec-jeb",
                    KerbalName = "Jeb",
                    KerbalEndStateField = KerbalEndState.Recovered,
                    StartUT = 0,
                    EndUT = 1000,
                    Sequence = 1
                }
            };

            module.PrePass(actions);
            module.ProcessAction(actions[0]);

            var allRecordingCrewField = typeof(KerbalsModule).GetField(
                "allRecordingCrew", BindingFlags.Instance | BindingFlags.NonPublic);
            Assert.NotNull(allRecordingCrewField);
            allRecordingCrewField.SetValue(module, new HashSet<string> { "Kirrim" });

            module.PostWalk();

            Assert.Contains("Kirrim", module.RetiredKerbals);
            Assert.DoesNotContain("Hanley", module.RetiredKerbals);
            Assert.Equal("Hanley", module.GetActiveOccupant("Jeb"));
        }

        [Fact]
        public void ApplyToRoster_DisplacedUnusedStandIn_IsNotRecreated()
        {
            var module = new KerbalsModule();
            var parent = new ConfigNode("TEST");
            var slotsNode = parent.AddNode("KERBAL_SLOTS");
            var slotNode = slotsNode.AddNode("SLOT");
            slotNode.AddValue("owner", "Jeb");
            slotNode.AddValue("trait", "Pilot");
            var first = slotNode.AddNode("CHAIN_ENTRY");
            first.AddValue("name", "Hanley");
            var second = slotNode.AddNode("CHAIN_ENTRY");
            second.AddValue("name", "Kirrim");
            module.LoadSlots(parent);

            var rec = MakeRecording("Ship", new[] { "Jeb" },
                TerminalState.Recovered, 1000);
            rec.RecordingId = "rec-jeb";
            RecordingStore.AddRecordingWithTreeForTesting(rec);

            var kerbals = KerbalsTestHelper.RecalculateModule(module);

            MethodInfo method = typeof(KerbalsModule).GetMethod(
                "ShouldEnsureChainEntryInRoster", BindingFlags.Instance | BindingFlags.NonPublic);
            Assert.NotNull(method);

            Assert.True((bool)method.Invoke(kerbals, new object[] { kerbals.Slots["Jeb"], 0 }));
            Assert.False((bool)method.Invoke(kerbals, new object[] { kerbals.Slots["Jeb"], 1 }));
        }

        [Fact]
        public void ApplyToRoster_DisplacedRetiredStandIn_IsStillRecreated()
        {
            var module = new KerbalsModule();
            var parent = new ConfigNode("TEST");
            var slotsNode = parent.AddNode("KERBAL_SLOTS");
            var slotNode = slotsNode.AddNode("SLOT");
            slotNode.AddValue("owner", "Jeb");
            slotNode.AddValue("trait", "Pilot");
            var first = slotNode.AddNode("CHAIN_ENTRY");
            first.AddValue("name", "Hanley");
            var second = slotNode.AddNode("CHAIN_ENTRY");
            second.AddValue("name", "Kirrim");
            module.LoadSlots(parent);

            var rec = MakeRecording("Ship", new[] { "Jeb" },
                TerminalState.Recovered, 1000);
            rec.RecordingId = "rec-jeb";
            RecordingStore.AddRecordingWithTreeForTesting(rec);

            var kerbals = KerbalsTestHelper.RecalculateModule(module);

            var allRecordingCrewField = typeof(KerbalsModule).GetField(
                "allRecordingCrew", BindingFlags.Instance | BindingFlags.NonPublic);
            Assert.NotNull(allRecordingCrewField);
            allRecordingCrewField.SetValue(kerbals, new HashSet<string> { "Kirrim" });

            MethodInfo method = typeof(KerbalsModule).GetMethod(
                "ShouldEnsureChainEntryInRoster", BindingFlags.Instance | BindingFlags.NonPublic);
            Assert.NotNull(method);

            Assert.True((bool)method.Invoke(kerbals, new object[] { kerbals.Slots["Jeb"], 1 }));
        }

        [Fact]
        public void Recalculate_RepairedStandInRecording_StillRetiresHistoricalStandIn()
        {
            var module = new KerbalsModule();
            var parent = new ConfigNode("TEST");
            var slotsNode = parent.AddNode("KERBAL_SLOTS");
            var slotNode = slotsNode.AddNode("SLOT");
            slotNode.AddValue("owner", "Jeb");
            slotNode.AddValue("trait", "Pilot");
            var first = slotNode.AddNode("CHAIN_ENTRY");
            first.AddValue("name", "Hanley");
            var second = slotNode.AddNode("CHAIN_ENTRY");
            second.AddValue("name", "Kirrim");
            module.LoadSlots(parent);
            LedgerOrchestrator.SetKerbalsForTesting(module);

            var ownerRec = MakeRecording("Owner Flight", new[] { "Jeb" },
                TerminalState.Recovered, 1000);
            ownerRec.RecordingId = "rec-owner";
            RecordingStore.AddRecordingWithTreeForTesting(ownerRec);

            var standInSnapshot = new ConfigNode("VESSEL");
            var standInPart = standInSnapshot.AddNode("PART");
            standInPart.AddValue("crew", "Kirrim");
            var standInRec = new Recording
            {
                RecordingId = "rec-standin",
                VesselName = "Stand-In Flight",
                GhostVisualSnapshot = standInSnapshot,
                ExplicitStartUT = 0,
                ExplicitEndUT = 1000,
                CrewEndStates = new Dictionary<string, KerbalEndState>
                {
                    { "Jeb", KerbalEndState.Recovered }
                }
            };
            RecordingStore.AddRecordingWithTreeForTesting(standInRec);

            var actions = new List<GameAction>();
            actions.AddRange(LedgerOrchestrator.CreateKerbalAssignmentActions("rec-owner", 0.0, 1000.0));
            actions.AddRange(LedgerOrchestrator.CreateKerbalAssignmentActions("rec-standin", 0.0, 1000.0));

            module.Reset();
            module.PrePass(actions);
            for (int i = 0; i < actions.Count; i++)
                module.ProcessAction(actions[i]);
            module.PostWalk();

            Assert.Contains("Kirrim", module.RetiredKerbals);
            Assert.DoesNotContain("Hanley", module.RetiredKerbals);
            Assert.Equal("Hanley", module.GetActiveOccupant("Jeb"));
        }

        // ── Serialization ──

        [Fact]
        public void SaveSlots_LoadSlots_RoundTrip()
        {
            // Load initial data
            var kerbals = new KerbalsModule();
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

            kerbals.LoadSlots(loadParent);

            // Verify loaded
            Assert.True(kerbals.Slots.ContainsKey("Jeb"));
            Assert.Equal("Pilot", kerbals.Slots["Jeb"].OwnerTrait);
            Assert.True(kerbals.Slots["Jeb"].OwnerPermanentlyGone);
            Assert.Equal(2, kerbals.Slots["Jeb"].Chain.Count);
            Assert.Equal("Hanley", kerbals.Slots["Jeb"].Chain[0]);
            Assert.Equal("Kirrim", kerbals.Slots["Jeb"].Chain[1]);

            // Save to new parent
            var saveParent = new ConfigNode("TEST2");
            kerbals.SaveSlots(saveParent);

            // Reset and reload
            kerbals.ResetForTesting();
            kerbals.LoadSlots(saveParent);

            Assert.True(kerbals.Slots.ContainsKey("Jeb"));
            Assert.Equal("Pilot", kerbals.Slots["Jeb"].OwnerTrait);
            Assert.True(kerbals.Slots["Jeb"].OwnerPermanentlyGone);
            Assert.Equal(2, kerbals.Slots["Jeb"].Chain.Count);
            Assert.Equal("Hanley", kerbals.Slots["Jeb"].Chain[0]);
            Assert.Equal("Kirrim", kerbals.Slots["Jeb"].Chain[1]);
        }

        [Fact]
        public void LoadSlots_EmptyParent_ClearsSlots()
        {
            // Pre-populate some slots
            var kerbals = new KerbalsModule();
            var parent = new ConfigNode("TEST");
            var slotsNode = parent.AddNode("KERBAL_SLOTS");
            var slotNode = slotsNode.AddNode("SLOT");
            slotNode.AddValue("owner", "Jeb");
            slotNode.AddValue("trait", "Pilot");
            kerbals.LoadSlots(parent);
            Assert.True(kerbals.Slots.ContainsKey("Jeb"));

            // Load from empty parent -> should clear
            var emptyParent = new ConfigNode("EMPTY");
            kerbals.LoadSlots(emptyParent);
            Assert.Empty(kerbals.Slots);
        }

        [Fact]
        public void Migration_OldCrewReplacements_LoadsAsChainDepth0()
        {
            var kerbals = new KerbalsModule();
            var parent = new ConfigNode("TEST");
            var crNode = parent.AddNode("CREW_REPLACEMENTS");
            var entry = crNode.AddNode("ENTRY");
            entry.AddValue("original", "Jeb");
            entry.AddValue("replacement", "Hanley");

            kerbals.LoadSlots(parent);

            Assert.True(kerbals.Slots.ContainsKey("Jeb"));
            Assert.Single(kerbals.Slots["Jeb"].Chain);
            Assert.Equal("Hanley", kerbals.Slots["Jeb"].Chain[0]);
            Assert.Equal("Pilot", kerbals.Slots["Jeb"].OwnerTrait); // default
        }

        [Fact]
        public void Migration_PreferNewFormat_OverLegacy()
        {
            // Both KERBAL_SLOTS and CREW_REPLACEMENTS exist -> new format wins
            var kerbals = new KerbalsModule();
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

            kerbals.LoadSlots(parent);

            // New format should be used
            Assert.Equal("Engineer", kerbals.Slots["Jeb"].OwnerTrait);
            Assert.Single(kerbals.Slots["Jeb"].Chain);
            Assert.Equal("NewStandIn", kerbals.Slots["Jeb"].Chain[0]);
        }

        [Fact]
        public void SaveSlots_NoSlots_DoesNotCreateNode()
        {
            var kerbals = new KerbalsModule();
            var parent = new ConfigNode("TEST");
            kerbals.SaveSlots(parent);

            Assert.Null(parent.GetNode("KERBAL_SLOTS"));
        }

        // ── Log assertions ──

        [Fact]
        public void Recalculate_LogsReservationCount()
        {
            var rec = MakeRecording("Ship", new[] { "Jeb", "Bill" },
                TerminalState.Recovered, 2000);
            RecordingStore.AddRecordingWithTreeForTesting(rec);

            KerbalsTestHelper.RecalculateFromStore();

            Assert.Contains(logLines, l =>
                l.Contains("[KerbalsModule]") && l.Contains("reservations=2"));
        }

        [Fact]
        public void Recalculate_LogsSlotCount()
        {
            var rec = MakeRecording("Ship", new[] { "Jeb" },
                TerminalState.Recovered, 2000);
            RecordingStore.AddRecordingWithTreeForTesting(rec);

            KerbalsTestHelper.RecalculateFromStore();

            Assert.Contains(logLines, l =>
                l.Contains("[KerbalsModule]") && l.Contains("slots=1"));
        }

        [Fact]
        public void IsKerbalAvailable_LogsReservedStatus()
        {
            var rec = MakeRecording("Ship", new[] { "Jeb" },
                TerminalState.Recovered, 2000);
            RecordingStore.AddRecordingWithTreeForTesting(rec);
            var kerbals = KerbalsTestHelper.RecalculateFromStore();

            kerbals.IsKerbalAvailable("Jeb");

            Assert.Contains(logLines, l =>
                l.Contains("[KerbalsModule]") && l.Contains("'Jeb'") && l.Contains("RESERVED"));
        }

        [Fact]
        public void IsKerbalAvailable_LogsAvailableStatus()
        {
            var kerbals = new KerbalsModule();
            kerbals.IsKerbalAvailable("Val");

            Assert.Contains(logLines, l =>
                l.Contains("[KerbalsModule]") && l.Contains("'Val'") && l.Contains("available"));
        }

        [Fact]
        public void LoadSlots_LogsMigrationCount()
        {
            var kerbals = new KerbalsModule();
            var parent = new ConfigNode("TEST");
            var crNode = parent.AddNode("CREW_REPLACEMENTS");
            var entry = crNode.AddNode("ENTRY");
            entry.AddValue("original", "Jeb");
            entry.AddValue("replacement", "Hanley");

            kerbals.LoadSlots(parent);

            Assert.Contains(logLines, l =>
                l.Contains("[KerbalsModule]") && l.Contains("Migrated") && l.Contains("1 slot"));
        }

        [Fact]
        public void SaveSlots_LogsSaveCount()
        {
            // Load a slot first
            var kerbals = new KerbalsModule();
            var parent = new ConfigNode("TEST");
            var slotsNode = parent.AddNode("KERBAL_SLOTS");
            var slotNode = slotsNode.AddNode("SLOT");
            slotNode.AddValue("owner", "Jeb");
            slotNode.AddValue("trait", "Pilot");
            kerbals.LoadSlots(parent);

            logLines.Clear();
            var saveParent = new ConfigNode("SAVE");
            kerbals.SaveSlots(saveParent);

            Assert.Contains(logLines, l =>
                l.Contains("[KerbalsModule]") && l.Contains("Saved 1 kerbal slot"));
        }

        // ── MIA Respawn Override ──
        // KSP has a built-in MIA respawn mechanic that transitions Dead kerbals
        // to Available after a delay. Parsek overrides this: ApplyToRoster sets
        // every reserved kerbal to Assigned, so even if KSP respawns a Dead
        // kerbal, the next RecalculateAndApply resets them to reserved state.
        // The tests below verify the reservation logic that underpins this override.

        [Fact]
        public void MiaRespawnOverride_DeadKerbal_StaysReservedAcrossRecalculations()
        {
            // A kerbal who died in a recording must remain permanently reserved.
            // Even if KSP's MIA respawn changes their rosterStatus to Available
            // between recalculation calls, Recalculate() re-derives the reservation
            // as permanent (Dead -> IsPermanent=true), and ApplyToRoster would
            // set them back to Assigned on the KSP roster.
            var rec = MakeRecording("Doomed Ship", new[] { "Jeb" },
                TerminalState.Destroyed, 1000);
            RecordingStore.AddRecordingWithTreeForTesting(rec);

            // First recalculation
            var kerbals = KerbalsTestHelper.RecalculateFromStore();
            Assert.True(kerbals.Reservations["Jeb"].IsPermanent);
            Assert.False(kerbals.IsKerbalAvailable("Jeb"));

            // Simulate passage of time / KSP MIA respawn by recalculating again.
            // The reservation should still be permanent and active.
            kerbals = KerbalsTestHelper.RecalculateModule(kerbals);
            Assert.True(kerbals.Reservations["Jeb"].IsPermanent);
            Assert.False(kerbals.IsKerbalAvailable("Jeb"));

            Assert.Contains(logLines, l =>
                l.Contains("[KerbalsModule]") && l.Contains("'Jeb'") && l.Contains("RESERVED"));
        }

        [Fact]
        public void MiaRespawnOverride_RecoveredKerbal_StaysReservedUntilEndUT()
        {
            // A recovered kerbal stays reserved until their recording's EndUT.
            // KSP cannot override this because ApplyToRoster sets Assigned status.
            var rec = MakeRecording("Safe Ship", new[] { "Val" },
                TerminalState.Recovered, 5000);
            RecordingStore.AddRecordingWithTreeForTesting(rec);

            var kerbals = KerbalsTestHelper.RecalculateFromStore();

            Assert.False(kerbals.Reservations["Val"].IsPermanent);
            Assert.Equal(5000.0, kerbals.Reservations["Val"].ReservedUntilUT);
            Assert.False(kerbals.IsKerbalAvailable("Val"));
        }

        [Fact]
        public void MiaRespawnOverride_NonReservedKerbal_NotAffected()
        {
            // Kerbals not in any recording must NOT be affected by the MIA override.
            // ApplyToRoster only iterates reservations dict — non-reserved kerbals
            // are never touched.
            var rec = MakeRecording("Ship", new[] { "Jeb" },
                TerminalState.Destroyed, 1000);
            RecordingStore.AddRecordingWithTreeForTesting(rec);

            var kerbals = KerbalsTestHelper.RecalculateFromStore();

            // Jeb is reserved (dead), but Val/Bill/Bob are not in any recording
            Assert.True(kerbals.IsKerbalAvailable("Val"));
            Assert.True(kerbals.IsKerbalAvailable("Bill"));
            Assert.True(kerbals.IsKerbalAvailable("Bob"));

            // Second recalculation should not introduce spurious reservations
            kerbals = KerbalsTestHelper.RecalculateModule(kerbals);
            Assert.True(kerbals.IsKerbalAvailable("Val"));
            Assert.True(kerbals.IsKerbalAvailable("Bill"));
            Assert.True(kerbals.IsKerbalAvailable("Bob"));
        }

        [Fact]
        public void MiaRespawnOverride_DeadKerbal_SurvivesMultipleRecalculations()
        {
            // Simulate KSP repeatedly respawning a dead kerbal by running
            // Recalculate many times. The reservation must remain permanent
            // every time — ApplyToRoster would reset rosterStatus to Assigned
            // on each cycle, preventing KSP's MIA respawn from taking effect.
            var rec = MakeRecording("Doomed", new[] { "Jeb" },
                TerminalState.Destroyed, 1000);
            RecordingStore.AddRecordingWithTreeForTesting(rec);

            var kerbals = new KerbalsModule();
            for (int i = 0; i < 5; i++)
            {
                kerbals = KerbalsTestHelper.RecalculateModule(kerbals);
                Assert.True(kerbals.Reservations.ContainsKey("Jeb"),
                    $"Jeb should be reserved on recalculation {i}");
                Assert.True(kerbals.Reservations["Jeb"].IsPermanent,
                    $"Jeb should be permanently reserved on recalculation {i}");
                Assert.False(kerbals.IsKerbalAvailable("Jeb"),
                    $"Jeb should not be available on recalculation {i}");
            }
        }

        [Fact]
        public void MiaRespawnOverride_MixedCrew_OnlyReservedAffected()
        {
            // When a recording has multiple crew and some die while others are
            // recovered, only the dead ones get permanent reservation. All crew
            // in the recording are reserved, but non-recording kerbals are free.
            var snapshot = new ConfigNode("VESSEL");
            var part = snapshot.AddNode("PART");
            part.AddValue("crew", "Jeb");
            part.AddValue("crew", "Val");

            // End snapshot only has Val (Jeb EVA'd and is presumed dead for Landed)
            var endSnapshot = new ConfigNode("VESSEL");
            var endPart = endSnapshot.AddNode("PART");
            endPart.AddValue("crew", "Val");

            var rec = new Recording
            {
                VesselName = "Mixed",
                VesselSnapshot = snapshot,
                GhostVisualSnapshot = snapshot,
                TerminalStateValue = TerminalState.Landed,
                ExplicitStartUT = 0,
                ExplicitEndUT = 2000,
            };

            // Manually set end states: Jeb=Dead (not in end snapshot), Val=Aboard
            rec.CrewEndStates = new Dictionary<string, KerbalEndState>
            {
                { "Jeb", KerbalEndState.Dead },
                { "Val", KerbalEndState.Aboard }
            };
            RecordingStore.AddRecordingWithTreeForTesting(rec);

            var kerbals = KerbalsTestHelper.RecalculateFromStore();

            // Jeb: permanent reservation (dead)
            Assert.True(kerbals.Reservations["Jeb"].IsPermanent);

            // Val: open-ended but not permanent (aboard)
            Assert.False(kerbals.Reservations["Val"].IsPermanent);
            Assert.Equal(double.PositiveInfinity, kerbals.Reservations["Val"].ReservedUntilUT);

            // Bill: not in any recording, completely free
            Assert.True(kerbals.IsKerbalAvailable("Bill"));
            Assert.False(kerbals.Reservations.ContainsKey("Bill"));
        }

        // ── Reset ──

        [Fact]
        public void ResetForTesting_ClearsAllState()
        {
            var rec = MakeRecording("Ship", new[] { "Jeb" },
                TerminalState.Recovered, 2000);
            RecordingStore.AddRecordingWithTreeForTesting(rec);
            var kerbals = KerbalsTestHelper.RecalculateFromStore();

            // Verify state exists
            Assert.NotEmpty(kerbals.Reservations);
            Assert.NotEmpty(kerbals.Slots);

            kerbals.ResetForTesting();

            Assert.Empty(kerbals.Reservations);
            Assert.Empty(kerbals.Slots);
            Assert.Empty(kerbals.RetiredKerbals);
        }

    }
}
