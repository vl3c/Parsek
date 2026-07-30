using System;
using System.Collections.Generic;
using System.Linq;
using Xunit;

namespace Parsek.Tests
{
    /// <summary>
    /// End-state suite for <see cref="KerbalsModule"/>: drives the module's
    /// recalculation lifecycle over ledgers that produce each kerbal end-state
    /// population (Dead, Recovered, Missing/Unknown, stand-in, MIA-respawn) and
    /// asserts the computed per-identity end state, the reservation shape that
    /// end state maps to, ordering/cutoff behaviour, and idempotence.
    ///
    /// Distinct from <c>KerbalEndStateTests</c> (which unit-tests the inference
    /// decision table in isolation) and <c>KerbalReservationTests</c> (which
    /// hand-sets <c>CrewEndStates</c> before recalculating): every fixture here
    /// goes through the production population + extraction path
    /// (<see cref="KerbalsModule.PopulateCrewEndStates(Recording)"/> ->
    /// <see cref="LedgerOrchestrator.ExtractCrewFromRecording"/> ->
    /// KerbalAssignment rows -> module walk), so a break anywhere along that
    /// chain reds here.
    /// </summary>
    [Collection("Sequential")] // KerbalsModule / RecordingStore / crewReplacements statics
    public class KerbalsModuleEndStateTests : IDisposable
    {
        private readonly List<string> logLines = new List<string>();

        public KerbalsModuleEndStateTests()
        {
            ParsekLog.ResetTestOverrides();
            ParsekLog.SuppressLogging = false;
            ParsekLog.TestSinkForTesting = line => logLines.Add(line);
            RecordingStore.SuppressLogging = true;
            RecordingStore.ResetForTesting();
            GameStateStore.SuppressLogging = true;
            GameStateStore.ResetForTesting();
            CrewReservationManager.ResetReplacementsForTesting();
            RecalculationEngine.ClearModules();
            // The slot-chain leg of ReverseMapCrewNames reads this static; pin it
            // to a known-empty module so a sibling class's leftover cannot leak in.
            LedgerOrchestrator.SetKerbalsForTesting(new KerbalsModule());
        }

        public void Dispose()
        {
            LedgerOrchestrator.SetKerbalsForTesting(new KerbalsModule());
            RecalculationEngine.ClearModules();
            CrewReservationManager.ResetReplacementsForTesting();
            GameStateStore.ResetForTesting();
            RecordingStore.ResetForTesting();
            RecordingStore.SuppressLogging = false;
            ParsekLog.ResetTestOverrides();
            ParsekLog.SuppressLogging = true;
        }

        // ========================================================
        // Fixture helpers
        // ========================================================

        private static ConfigNode CrewSnapshot(params string[] crew)
        {
            var snapshot = new ConfigNode("VESSEL");
            var part = snapshot.AddNode("PART");
            for (int i = 0; i < crew.Length; i++)
                part.AddValue("crew", crew[i]);
            return snapshot;
        }

        /// <summary>
        /// Builds a flight recording with an explicit start-of-recording crew
        /// roster and an explicit end-of-recording crew roster. End states are
        /// NOT pre-populated: the suite drives the real inference.
        /// </summary>
        private static Recording MakeFlight(
            string id,
            string[] startCrew,
            string[] endCrew,
            TerminalState? terminal,
            double startUT,
            double endUT)
        {
            return new Recording
            {
                RecordingId = id,
                VesselName = id,
                GhostVisualSnapshot = CrewSnapshot(startCrew),
                VesselSnapshot = endCrew == null ? null : CrewSnapshot(endCrew),
                TerminalStateValue = terminal,
                ExplicitStartUT = startUT,
                ExplicitEndUT = endUT,
            };
        }

        private static Recording AddFlight(
            string id,
            string[] startCrew,
            string[] endCrew,
            TerminalState? terminal,
            double startUT = 100.0,
            double endUT = 2000.0)
        {
            var rec = MakeFlight(id, startCrew, endCrew, terminal, startUT, endUT);
            RecordingStore.AddRecordingWithTreeForTesting(rec);
            return rec;
        }

        /// <summary>
        /// Builds KerbalAssignment rows for one recording through the production
        /// extraction path (<see cref="LedgerOrchestrator.ExtractCrewFromRecording"/>),
        /// which reverse-maps stand-in names, drops Tourists, and reads the
        /// populated end state per identity.
        /// </summary>
        private static List<GameAction> BuildAssignmentActions(Recording rec)
        {
            var actions = new List<GameAction>();
            if (rec == null) return actions;

            KerbalsModule.PopulateCrewEndStates(rec);

            var crew = LedgerOrchestrator.ExtractCrewFromRecording(rec);
            for (int i = 0; i < crew.Count; i++)
            {
                actions.Add(new GameAction
                {
                    UT = rec.StartUT,
                    Type = GameActionType.KerbalAssignment,
                    RecordingId = rec.RecordingId,
                    KerbalName = crew[i].Name,
                    KerbalRole = crew[i].Role,
                    StartUT = (float)rec.StartUT,
                    EndUT = (float)rec.EndUT,
                    KerbalEndStateField = crew[i].EndState,
                    Sequence = i + 1
                });
            }
            return actions;
        }

        /// <summary>
        /// Every KerbalAssignment row the current committed store produces,
        /// in store order.
        /// </summary>
        private static List<GameAction> BuildAssignmentActionsForStore()
        {
            var actions = new List<GameAction>();
            var recordings = RecordingStore.CommittedRecordings;
            for (int i = 0; i < recordings.Count; i++)
                actions.AddRange(BuildAssignmentActions(recordings[i]));
            return actions;
        }

        /// <summary>
        /// Runs the full IResourceModule lifecycle over an explicit action list.
        /// Mirrors <see cref="RecalculationEngine"/>'s dispatch order without
        /// pulling the other eight modules into the fixture.
        /// </summary>
        private static KerbalsModule Walk(KerbalsModule module, List<GameAction> actions)
        {
            module.Reset();
            module.PrePass(actions);
            for (int i = 0; i < actions.Count; i++)
                module.ProcessAction(actions[i]);
            module.PostWalk();
            return module;
        }

        private static KerbalsModule WalkStore(KerbalsModule module = null)
        {
            return Walk(module ?? new KerbalsModule(), BuildAssignmentActionsForStore());
        }

        // ========================================================
        // Population: Dead
        // ========================================================

        [Fact]
        public void Dead_VesselDestroyedWithCrewAboard_IsPermanentlyReserved()
        {
            var rec = AddFlight("dead-destroyed", new[] { "Jeb", "Bill" },
                new[] { "Jeb", "Bill" }, TerminalState.Destroyed, 100, 900);

            var kerbals = WalkStore();

            Assert.Equal(KerbalEndState.Dead, rec.CrewEndStates["Jeb"]);
            Assert.Equal(KerbalEndState.Dead, rec.CrewEndStates["Bill"]);

            foreach (var name in new[] { "Jeb", "Bill" })
            {
                Assert.True(kerbals.Reservations[name].IsPermanent);
                Assert.Equal(double.PositiveInfinity, kerbals.Reservations[name].ReservedUntilUT);
                Assert.False(kerbals.IsKerbalAvailable(name));
                Assert.Equal(KerbalReservationKind.ReservedActive, kerbals.GetReservationKind(name));
                Assert.True(kerbals.ShouldFilterFromCrewDialog(name));
                // A permanent loss exits the chain system: no stand-in is owed.
                Assert.False(kerbals.Slots.ContainsKey(name));
            }
        }

        [Fact]
        public void Dead_CrewLostOffAnIntactVessel_IsPermanentWhileSurvivorStaysAboard()
        {
            // Jeb EVA'd and never came back; Bill is still in the end snapshot.
            var rec = AddFlight("dead-eva-loss", new[] { "Jeb", "Bill" },
                new[] { "Bill" }, TerminalState.Landed, 100, 2000);

            var kerbals = WalkStore();

            Assert.Equal(KerbalEndState.Dead, rec.CrewEndStates["Jeb"]);
            Assert.Equal(KerbalEndState.Aboard, rec.CrewEndStates["Bill"]);

            Assert.True(kerbals.Reservations["Jeb"].IsPermanent);

            // Aboard is open-ended but recoverable: not permanent, so the slot
            // system still owes Bill a stand-in.
            Assert.False(kerbals.Reservations["Bill"].IsPermanent);
            Assert.Equal(double.PositiveInfinity, kerbals.Reservations["Bill"].ReservedUntilUT);
            Assert.True(kerbals.Slots.ContainsKey("Bill"));
            Assert.False(kerbals.Slots.ContainsKey("Jeb"));
        }

        [Fact]
        public void Dead_DeathRowDroppedFromEffectiveLedger_KerbalReturnsToAvailable()
        {
            // A supersede/tombstone removes the fatal recording from the
            // effective ledger. The next walk on the SAME module instance must
            // release the permanent reservation and clear the sticky
            // permanently-gone flag on the surviving slot.
            var module = new KerbalsModule();
            var parent = new ConfigNode("TEST");
            var slotNode = parent.AddNode("KERBAL_SLOTS").AddNode("SLOT");
            slotNode.AddValue("owner", "Jeb");
            slotNode.AddValue("trait", "Pilot");
            slotNode.AddNode("CHAIN_ENTRY").AddValue("name", "Hanley Kerman");
            module.LoadSlots(parent);

            AddFlight("dead-superseded", new[] { "Jeb" },
                new[] { "Jeb" }, TerminalState.Destroyed, 100, 900);
            var survivor = AddFlight("survivor", new[] { "Jeb" },
                new[] { "Jeb" }, TerminalState.Recovered, 100, 900);

            WalkStore(module);
            Assert.True(module.Reservations["Jeb"].IsPermanent);
            Assert.True(module.Slots["Jeb"].OwnerPermanentlyGone);
            Assert.Null(module.GetActiveOccupant("Jeb"));

            // Re-walk with only the surviving rows (the tombstoned death row gone).
            Walk(module, BuildAssignmentActions(survivor));

            Assert.False(module.Reservations["Jeb"].IsPermanent);
            Assert.Equal(900.0, module.Reservations["Jeb"].ReservedUntilUT);
            Assert.False(module.Slots["Jeb"].OwnerPermanentlyGone);
            Assert.Equal("Hanley Kerman", module.GetActiveOccupant("Jeb"));

            // And with every row gone the kerbal is free again.
            Walk(module, new List<GameAction>());
            Assert.True(module.IsKerbalAvailable("Jeb"));
            Assert.Equal(KerbalReservationKind.NotManaged, module.GetReservationKind("Jeb"));
            Assert.Equal("Jeb", module.GetActiveOccupant("Jeb"));
        }

        // ========================================================
        // Population: Recovered
        // ========================================================

        [Fact]
        public void Recovered_ReservationEndsAtRecordingEndUT()
        {
            var rec = AddFlight("recovered", new[] { "Val" },
                new[] { "Val" }, TerminalState.Recovered, 100, 5000);

            var kerbals = WalkStore();

            Assert.Equal(KerbalEndState.Recovered, rec.CrewEndStates["Val"]);
            Assert.False(kerbals.Reservations["Val"].IsPermanent);
            Assert.Equal(5000.0, kerbals.Reservations["Val"].ReservedUntilUT);
            Assert.True(kerbals.Slots.ContainsKey("Val"));
        }

        [Fact]
        public void Recovered_ReservationUsesDoubleEndUT_NotTheActionsFloatEndUT()
        {
            // The module deliberately reads the recording's double EndUT rather
            // than GameAction.EndUT (a float). At career-scale UTs the float
            // rounds by tens of seconds; a regression that swapped the source
            // would land on the rounded value.
            const double endUT = 123456789.5;
            var rec = AddFlight("recovered-precision", new[] { "Val" },
                new[] { "Val" }, TerminalState.Recovered, 100, endUT);

            var actions = BuildAssignmentActions(rec);
            Assert.NotEqual(endUT, (double)actions[0].EndUT); // fixture precondition

            var kerbals = Walk(new KerbalsModule(), actions);

            Assert.Equal(endUT, kerbals.Reservations["Val"].ReservedUntilUT);
        }

        [Fact]
        public void Recovered_ThenDeadInAnotherRecording_PermanentWinsRegardlessOfRowOrder()
        {
            AddFlight("rec-a-recovered", new[] { "Jeb" },
                new[] { "Jeb" }, TerminalState.Recovered, 100, 5000);
            AddFlight("rec-b-destroyed", new[] { "Jeb" },
                new[] { "Jeb" }, TerminalState.Destroyed, 6000, 7000);

            var forward = BuildAssignmentActionsForStore();
            var reversed = new List<GameAction>(forward);
            reversed.Reverse();

            var a = Walk(new KerbalsModule(), forward);
            var b = Walk(new KerbalsModule(), reversed);

            foreach (var kerbals in new[] { a, b })
            {
                Assert.True(kerbals.Reservations["Jeb"].IsPermanent);
                Assert.Equal(double.PositiveInfinity, kerbals.Reservations["Jeb"].ReservedUntilUT);
            }
        }

        [Fact]
        public void Recovered_TwoRecordings_ReservationTakesTheLaterEndUT_RegardlessOfRowOrder()
        {
            AddFlight("early", new[] { "Bob" }, new[] { "Bob" },
                TerminalState.Recovered, 100, 2000);
            AddFlight("late", new[] { "Bob" }, new[] { "Bob" },
                TerminalState.Recovered, 3000, 9000);

            var forward = BuildAssignmentActionsForStore();
            var reversed = new List<GameAction>(forward);
            reversed.Reverse();

            Assert.Equal(9000.0,
                Walk(new KerbalsModule(), forward).Reservations["Bob"].ReservedUntilUT);
            Assert.Equal(9000.0,
                Walk(new KerbalsModule(), reversed).Reservations["Bob"].ReservedUntilUT);
        }

        // ========================================================
        // Population: Missing (crew left the recording to an unknown fate)
        // ========================================================

        [Theory]
        [InlineData(TerminalState.Docked)]
        [InlineData(TerminalState.Boarded)]
        public void Missing_CrewTransferredOffTheVessel_IsUnknownAndStaysOpenEnded(
            TerminalState terminal)
        {
            // Crew present at recording start, absent from the end snapshot on a
            // transfer terminal state: the module cannot see where they went, so
            // the reservation must stay open-ended rather than expiring at EndUT.
            var rec = AddFlight("missing-" + terminal, new[] { "Jeb", "Bill" },
                new[] { "Bill" }, terminal, 100, 2000);

            var kerbals = WalkStore();

            Assert.Equal(KerbalEndState.Unknown, rec.CrewEndStates["Jeb"]);
            Assert.Equal(KerbalEndState.Aboard, rec.CrewEndStates["Bill"]);

            Assert.False(kerbals.Reservations["Jeb"].IsPermanent);
            Assert.Equal(double.PositiveInfinity, kerbals.Reservations["Jeb"].ReservedUntilUT);
            Assert.False(kerbals.IsKerbalAvailable("Jeb"));
        }

        [Fact]
        public void Missing_RecordingWithNoTerminalState_IsUnknownAndStaysOpenEnded()
        {
            // Still-in-flight recording: no terminal state at all.
            var rec = AddFlight("missing-inflight", new[] { "Val" },
                new[] { "Val" }, null, 100, 2000);

            var kerbals = WalkStore();

            Assert.Equal(KerbalEndState.Unknown, rec.CrewEndStates["Val"]);
            Assert.Equal(double.PositiveInfinity, kerbals.Reservations["Val"].ReservedUntilUT);
            Assert.False(kerbals.Reservations["Val"].IsPermanent);
        }

        [Fact]
        public void Missing_UnknownDoesNotDowngradeAnEarlierPermanentLoss()
        {
            AddFlight("fatal", new[] { "Jeb" }, new[] { "Jeb" },
                TerminalState.Destroyed, 100, 900);
            AddFlight("later-unknown", new[] { "Jeb" }, new[] { "Bill" },
                TerminalState.Docked, 1000, 2000);

            var kerbals = WalkStore();

            Assert.True(kerbals.Reservations["Jeb"].IsPermanent);
        }

        // ========================================================
        // Population: ghost-only chain handoff
        // ========================================================

        [Theory]
        // chain + ghost-only + a handoff-shaped terminal state -> use the handoff rule
        [InlineData("chain-1", true, false, null, true)]
        [InlineData("chain-1", true, false, TerminalState.Boarded, true)]
        [InlineData("chain-1", true, false, TerminalState.Destroyed, true)]
        [InlineData("chain-1", true, false, TerminalState.Recovered, true)]
        // a real end-of-flight terminal state is not a handoff
        [InlineData("chain-1", true, false, TerminalState.Landed, false)]
        [InlineData("chain-1", true, false, TerminalState.Orbiting, false)]
        // not part of a chain
        [InlineData(null, true, false, null, false)]
        // an end-of-recording vessel snapshot exists -> ordinary inference applies
        [InlineData("chain-1", true, true, null, false)]
        // no start-crew source at all
        [InlineData("chain-1", false, false, null, false)]
        public void ShouldUseGhostOnlyChainHandoffEndState_TruthTable(
            string chainId, bool hasGhostSnapshot, bool hasVesselSnapshot,
            TerminalState? terminal, bool expected)
        {
            var rec = new Recording
            {
                RecordingId = "handoff-probe",
                ChainId = chainId,
                GhostVisualSnapshot = hasGhostSnapshot ? CrewSnapshot("Jeb") : null,
                VesselSnapshot = hasVesselSnapshot ? CrewSnapshot("Jeb") : null,
                TerminalStateValue = terminal,
            };

            Assert.Equal(expected, KerbalsModule.ShouldUseGhostOnlyChainHandoffEndState(rec));
        }

        [Fact]
        public void ShouldUseGhostOnlyChainHandoffEndState_EvaCrewNameIsAValidStartCrewSource()
        {
            var rec = new Recording
            {
                RecordingId = "handoff-eva",
                ChainId = "chain-eva",
                GhostVisualSnapshot = null,
                VesselSnapshot = null,
                EvaCrewName = "Jeb",
                TerminalStateValue = null,
            };

            Assert.True(KerbalsModule.ShouldUseGhostOnlyChainHandoffEndState(rec));
        }

        [Fact]
        public void ShouldUseGhostOnlyChainHandoffEndState_NullRecording_IsFalse()
        {
            Assert.False(KerbalsModule.ShouldUseGhostOnlyChainHandoffEndState(null));
        }

        [Theory]
        [InlineData(TerminalState.Destroyed, KerbalEndState.Dead)]
        [InlineData(TerminalState.Recovered, KerbalEndState.Recovered)]
        [InlineData(TerminalState.Boarded, KerbalEndState.Recovered)]
        [InlineData(null, KerbalEndState.Recovered)]
        public void InferGhostOnlyChainHandoffEndState_MapsDestroyedToDeadAndEverythingElseToRecovered(
            TerminalState? terminal, KerbalEndState expected)
        {
            Assert.Equal(expected, KerbalsModule.InferGhostOnlyChainHandoffEndState(terminal));
        }

        [Fact]
        public void GhostOnlyChainHandoff_UnterminatedSegment_ProducesFiniteReservationNotIndefinite()
        {
            // A chain segment that ends at an internal handoff has no vessel
            // snapshot and no terminal state. Ordinary inference would call that
            // Unknown and reserve the crew forever; the handoff rule calls it
            // Recovered so a later committed segment extends the chain instead.
            var rec = new Recording
            {
                RecordingId = "handoff-segment",
                VesselName = "Handoff Segment",
                ChainId = "chain-handoff",
                GhostVisualSnapshot = CrewSnapshot("Jeb"),
                VesselSnapshot = null,
                TerminalStateValue = null,
                ExplicitStartUT = 100,
                ExplicitEndUT = 2000,
            };
            RecordingStore.AddRecordingWithTreeForTesting(rec);

            var kerbals = WalkStore();

            Assert.Equal(KerbalEndState.Recovered, rec.CrewEndStates["Jeb"]);
            Assert.False(kerbals.Reservations["Jeb"].IsPermanent);
            Assert.Equal(2000.0, kerbals.Reservations["Jeb"].ReservedUntilUT);
        }

        [Fact]
        public void GhostOnlyChainHandoff_DestroyedSegment_StillProducesPermanentReservation()
        {
            var rec = new Recording
            {
                RecordingId = "handoff-fatal",
                VesselName = "Handoff Fatal",
                ChainId = "chain-handoff-fatal",
                GhostVisualSnapshot = CrewSnapshot("Jeb"),
                VesselSnapshot = null,
                TerminalStateValue = TerminalState.Destroyed,
                ExplicitStartUT = 100,
                ExplicitEndUT = 2000,
            };
            RecordingStore.AddRecordingWithTreeForTesting(rec);

            var kerbals = WalkStore();

            Assert.Equal(KerbalEndState.Dead, rec.CrewEndStates["Jeb"]);
            Assert.True(kerbals.Reservations["Jeb"].IsPermanent);
        }

        // ========================================================
        // Population: stand-ins
        // ========================================================

        [Fact]
        public void StandIn_ReservedOwner_GetsGeneratedStandInAndReplacementBridgeEntry()
        {
            AddFlight("standin-source", new[] { "Jeb" }, new[] { "Jeb" },
                TerminalState.Recovered, 100, 2000);

            var kerbals = WalkStore();
            var roster = new FakeRoster();
            kerbals.ApplyToRoster(roster);

            var standIn = kerbals.Slots["Jeb"].Chain[0];
            Assert.False(string.IsNullOrEmpty(standIn));
            Assert.NotEqual("Jeb", standIn);
            Assert.True(roster.Contains(standIn));

            // The bridge SwapReservedCrewInFlight reads must map the reserved
            // owner to the active occupant.
            Assert.Equal(standIn, CrewReservationManager.CrewReplacements["Jeb"]);
            Assert.Equal(standIn, kerbals.GetActiveOccupant("Jeb"));
        }

        [Fact]
        public void StandIn_SeatedInALaterRecording_EndStateIsAttributedToTheOriginalOwner()
        {
            // #254: once a stand-in is seated on the live vessel, the next
            // recording's snapshot carries the STAND-IN's name. The end state
            // must be attributed to the original owner, and the stand-in must
            // not acquire a reservation of its own (which would cascade a
            // second-depth replacement).
            CrewReservationManager.SetReplacement("Jeb", "Hanley Kerman");

            var rec = AddFlight("standin-seated", new[] { "Hanley Kerman" },
                new[] { "Hanley Kerman" }, TerminalState.Recovered, 100, 2000);

            var actions = BuildAssignmentActions(rec);

            Assert.Single(actions);
            Assert.Equal("Jeb", actions[0].KerbalName);
            Assert.Equal(KerbalEndState.Recovered, rec.CrewEndStates["Jeb"]);
            Assert.False(rec.CrewEndStates.ContainsKey("Hanley Kerman"));

            var kerbals = Walk(new KerbalsModule(), actions);
            Assert.True(kerbals.Reservations.ContainsKey("Jeb"));
            Assert.False(kerbals.Reservations.ContainsKey("Hanley Kerman"));
        }

        /// <summary>
        /// Builds the "displaced but used" stand-in fixture: a two-seat flight
        /// whose snapshot carries stand-in "Hanley Kerman" alongside "Bill".
        /// The production extraction reverse-maps Hanley to slot owner "Jeb",
        /// so the recording contributes rows for Bill and Jeb while its RAW crew
        /// still names Hanley. Dropping Jeb's row (the effect of a supersede /
        /// tombstone on the owner's flight) frees the owner, which displaces the
        /// depth-0 stand-in who is nonetheless still present in a recording.
        /// </summary>
        private static List<GameAction> BuildDisplacedStandInFixture(KerbalsModule module)
        {
            var parent = new ConfigNode("TEST");
            var slotNode = parent.AddNode("KERBAL_SLOTS").AddNode("SLOT");
            slotNode.AddValue("owner", "Jeb");
            slotNode.AddValue("trait", "Pilot");
            slotNode.AddNode("CHAIN_ENTRY").AddValue("name", "Hanley Kerman");
            module.LoadSlots(parent);

            // The slot-chain leg of ReverseMapCrewNames reads this static.
            LedgerOrchestrator.SetKerbalsForTesting(module);

            AddFlight("two-seat", new[] { "Bill", "Hanley Kerman" },
                new[] { "Bill", "Hanley Kerman" }, TerminalState.Recovered, 100, 2000);

            var actions = BuildAssignmentActionsForStore();
            Assert.Contains(actions, a => a.KerbalName == "Jeb");   // reverse-mapped
            Assert.DoesNotContain(actions, a => a.KerbalName == "Hanley Kerman");
            return actions;
        }

        [Fact]
        public void StandIn_StillHoldingTheSlot_IsTheActiveOccupantAndNotRetired()
        {
            var module = new KerbalsModule();
            var actions = BuildDisplacedStandInFixture(module);

            Walk(module, actions);

            // The owner is reserved, so the free depth-0 stand-in occupies the
            // slot: active, not retired.
            Assert.True(module.Reservations.ContainsKey("Jeb"));
            Assert.Equal("Hanley Kerman", module.GetActiveOccupant("Jeb"));
            Assert.DoesNotContain("Hanley Kerman", module.RetiredKerbals);
            Assert.Equal(KerbalReservationKind.NotManaged,
                module.GetReservationKind("Hanley Kerman"));
            Assert.True(module.IsManaged("Hanley Kerman"));
            Assert.False(module.ShouldFilterFromCrewDialog("Hanley Kerman"));
        }

        [Fact]
        public void StandIn_DisplacedByTheOwnerReturning_IsRetiredNotReserved()
        {
            var module = new KerbalsModule();
            var actions = BuildDisplacedStandInFixture(module);

            // The owner's own row is superseded away -> the owner is free again
            // and reclaims the slot, displacing the stand-in who already flew.
            actions.RemoveAll(a => a.KerbalName == "Jeb");
            Walk(module, actions);

            Assert.False(module.Reservations.ContainsKey("Jeb"));
            Assert.False(module.Reservations.ContainsKey("Hanley Kerman"));
            Assert.True(module.IsKerbalInAnyRecording("Hanley Kerman"));
            Assert.Contains("Hanley Kerman", module.RetiredKerbals);
            Assert.Equal(KerbalReservationKind.ReservedRetired,
                module.GetReservationKind("Hanley Kerman"));
            Assert.True(module.IsManaged("Hanley Kerman"));
            Assert.True(module.ShouldFilterFromCrewDialog("Hanley Kerman"));
            Assert.Equal("Jeb", module.GetActiveOccupant("Jeb"));
        }

        [Fact]
        public void GetRetiredKerbals_ReturnsADefensiveSnapshot()
        {
            var module = new KerbalsModule();
            var actions = BuildDisplacedStandInFixture(module);
            actions.RemoveAll(a => a.KerbalName == "Jeb");
            Walk(module, actions);

            var snapshot = module.GetRetiredKerbals();
            Assert.Contains("Hanley Kerman", snapshot);

            // Mutating the returned list must not touch module state.
            ((List<string>)snapshot).Clear();
            Assert.Contains("Hanley Kerman", module.RetiredKerbals);

            // And a subsequent walk must not mutate a previously handed-out list.
            var held = module.GetRetiredKerbals();
            Walk(module, new List<GameAction>());
            Assert.Contains("Hanley Kerman", held);
            Assert.Empty(module.RetiredKerbals);
        }

        // ========================================================
        // Population: MIA respawn
        // ========================================================

        [Fact]
        public void MiaRespawn_KspFlippedTheDeadKerbalBackToAvailable_ModuleKeepsThemReserved()
        {
            AddFlight("mia-source", new[] { "Jeb" }, new[] { "Jeb" },
                TerminalState.Destroyed, 100, 900);

            var kerbals = WalkStore();

            // KSP's MIA respawn puts Jeb back on the roster as Available.
            var roster = new FakeRoster();
            roster.Add("Jeb", ProtoCrewMember.RosterStatus.Available);
            kerbals.ApplyToRoster(roster);

            // The module does not touch rosterStatus; it keeps the reservation
            // so the crew-dialog filter continues to hide him.
            Assert.True(roster.Contains("Jeb"));
            Assert.Equal(ProtoCrewMember.RosterStatus.Available, roster.StatusOf("Jeb"));
            Assert.True(kerbals.Reservations["Jeb"].IsPermanent);
            Assert.True(kerbals.ShouldFilterFromCrewDialog("Jeb"));
            Assert.Equal(KerbalReservationKind.ReservedActive, kerbals.GetReservationKind("Jeb"));

            // Re-deriving from the same ledger reproduces the same verdict.
            WalkStore(kerbals);
            Assert.True(kerbals.Reservations["Jeb"].IsPermanent);
            Assert.False(kerbals.IsKerbalAvailable("Jeb"));
        }

        [Fact]
        public void MiaRespawn_TombstoneCleanupPreservesAKerbalWhoIsStillReserved()
        {
            AddFlight("still-flying", new[] { "Rescuee Kerman" }, new[] { "Rescuee Kerman" },
                TerminalState.Recovered, 100, 2000);

            var kerbals = WalkStore();
            var roster = new FakeRoster();
            roster.Add("Rescuee Kerman", ProtoCrewMember.RosterStatus.Available);

            kerbals.QueueTombstonedRosterKerbal("Rescuee Kerman");
            kerbals.ApplyToRoster(roster);

            Assert.True(roster.Contains("Rescuee Kerman"));
            Assert.Contains(logLines, l => l.Contains("[KerbalsModule]")
                && l.Contains("Tombstoned roster cleanup:")
                && l.Contains("preserved=1"));
        }

        [Fact]
        public void MiaRespawn_TombstoneCleanupPreservesAKerbalStillPresentInARecording()
        {
            // The kerbal holds no reservation of their own any more (their rows
            // were tombstoned), but they still appear in a committed recording's
            // crew, so the roster entry must survive.
            AddFlight("crewed", new[] { "Jeb", "Ghosted Kerman" },
                new[] { "Jeb", "Ghosted Kerman" }, TerminalState.Recovered, 100, 2000);

            var actions = BuildAssignmentActionsForStore();
            actions.RemoveAll(a => a.KerbalName == "Ghosted Kerman");

            var module = Walk(new KerbalsModule(), actions);
            Assert.False(module.Reservations.ContainsKey("Ghosted Kerman"));
            Assert.True(module.IsKerbalInAnyRecording("Ghosted Kerman"));

            var roster = new FakeRoster();
            roster.Add("Ghosted Kerman", ProtoCrewMember.RosterStatus.Available);
            module.QueueTombstonedRosterKerbal("Ghosted Kerman");
            module.ApplyToRoster(roster);

            Assert.True(roster.Contains("Ghosted Kerman"));
            Assert.Contains(logLines, l => l.Contains("[KerbalsModule]")
                && l.Contains("Tombstoned roster cleanup:")
                && l.Contains("preserved=1"));
        }

        // ========================================================
        // Roster-creating rows (hire / rescue / stand-in)
        // ========================================================

        [Theory]
        [InlineData(GameActionType.KerbalHire, true)]
        [InlineData(GameActionType.KerbalRescue, true)]
        [InlineData(GameActionType.KerbalStandIn, true)]
        [InlineData(GameActionType.KerbalAssignment, false)]
        [InlineData(GameActionType.FundsSpending, false)]
        [InlineData(GameActionType.ReputationPenalty, false)]
        public void TryGetRosterCreatedKerbalName_OnlyRosterCreatingTypesQualify(
            GameActionType type, bool expected)
        {
            string name;
            bool got = KerbalsModule.TryGetRosterCreatedKerbalName(
                new GameAction { Type = type, KerbalName = "Bob Kerman" }, out name);

            Assert.Equal(expected, got);
            Assert.Equal(expected ? "Bob Kerman" : null, name);
        }

        [Fact]
        public void TryGetRosterCreatedKerbalName_NullActionOrEmptyName_IsFalse()
        {
            string name;
            Assert.False(KerbalsModule.TryGetRosterCreatedKerbalName(null, out name));
            Assert.Null(name);

            Assert.False(KerbalsModule.TryGetRosterCreatedKerbalName(
                new GameAction { Type = GameActionType.KerbalHire, KerbalName = "" }, out name));
        }

        [Fact]
        public void ProcessAction_RosterCreatingRow_TracksTheKerbalWithoutReservingThem()
        {
            var module = new KerbalsModule();
            module.Reset();
            module.PrePass(new List<GameAction>());
            module.ProcessAction(new GameAction
            {
                Type = GameActionType.KerbalHire,
                KerbalName = "Newbie Kerman",
                RecordingId = "some-rec",
            });
            module.PostWalk();

            Assert.Contains("Newbie Kerman", module.LedgerCreatedKerbals);
            Assert.True(module.IsKerbalAvailable("Newbie Kerman"));
            Assert.False(module.IsManaged("Newbie Kerman"));
        }

        [Fact]
        public void QueueTombstonedRosterKerbals_QueuesOnlyTheRosterCreatingRows()
        {
            var module = new KerbalsModule();
            module.QueueTombstonedRosterKerbals(new List<GameAction>
            {
                new GameAction { Type = GameActionType.KerbalHire, KerbalName = "Hired Kerman" },
                new GameAction { Type = GameActionType.KerbalRescue, KerbalName = "Rescued Kerman" },
                new GameAction { Type = GameActionType.KerbalAssignment, KerbalName = "Assigned Kerman" },
                new GameAction { Type = GameActionType.FundsSpending, KerbalName = "Irrelevant Kerman" },
            });

            module.ApplyToRoster(new FakeRoster());

            // candidates counts what actually entered the queue.
            Assert.Contains(logLines, l => l.Contains("[KerbalsModule]")
                && l.Contains("Tombstoned roster cleanup:")
                && l.Contains("candidates=2"));
        }

        [Fact]
        public void QueueTombstonedRosterKerbals_NullList_IsANoOp()
        {
            var module = new KerbalsModule();
            module.QueueTombstonedRosterKerbals(null);
            module.ApplyToRoster(new FakeRoster());

            Assert.DoesNotContain(logLines, l => l.Contains("Tombstoned roster cleanup:"));
        }

        // ========================================================
        // Ordering and UT cutoff
        // ========================================================

        [Fact]
        public void Cutoff_RowsAfterTheCutoffAreExcludedFromTheEndStateWalk()
        {
            var kerbals = new KerbalsModule();
            RecalculationEngine.RegisterModule(kerbals, RecalculationEngine.ModuleTier.FirstTier);

            AddFlight("early-flight", new[] { "Jeb" }, new[] { "Jeb" },
                TerminalState.Recovered, 100, 900);
            AddFlight("late-flight", new[] { "Val" }, new[] { "Val" },
                TerminalState.Recovered, 5000, 6000);
            var actions = BuildAssignmentActionsForStore();

            RecalculationEngine.Recalculate(actions, 1000.0);

            Assert.True(kerbals.Reservations.ContainsKey("Jeb"));
            Assert.False(kerbals.Reservations.ContainsKey("Val"));
            Assert.True(kerbals.IsKerbalAvailable("Val"));

            // The uncut walk sees both.
            RecalculationEngine.Recalculate(actions);
            Assert.True(kerbals.Reservations.ContainsKey("Jeb"));
            Assert.True(kerbals.Reservations.ContainsKey("Val"));
        }

        [Fact]
        public void Cutoff_ADeathAfterTheCutoffDoesNotMakeTheKerbalPermanentlyGone()
        {
            var kerbals = new KerbalsModule();
            RecalculationEngine.RegisterModule(kerbals, RecalculationEngine.ModuleTier.FirstTier);

            AddFlight("survived", new[] { "Jeb" }, new[] { "Jeb" },
                TerminalState.Recovered, 100, 900);
            AddFlight("died-later", new[] { "Jeb" }, new[] { "Jeb" },
                TerminalState.Destroyed, 5000, 6000);
            var actions = BuildAssignmentActionsForStore();

            RecalculationEngine.Recalculate(actions, 1000.0);

            Assert.False(kerbals.Reservations["Jeb"].IsPermanent);
            Assert.Equal(900.0, kerbals.Reservations["Jeb"].ReservedUntilUT);
            Assert.False(kerbals.Slots["Jeb"].OwnerPermanentlyGone);

            RecalculationEngine.Recalculate(actions);

            Assert.True(kerbals.Reservations["Jeb"].IsPermanent);
        }

        [Fact]
        public void ProcessAction_OrphanedRowForAnUnknownRecording_ProducesNoReservation()
        {
            AddFlight("known", new[] { "Jeb" }, new[] { "Jeb" },
                TerminalState.Recovered, 100, 2000);

            var actions = BuildAssignmentActionsForStore();
            actions.Add(new GameAction
            {
                UT = 100,
                Type = GameActionType.KerbalAssignment,
                RecordingId = "recording-that-does-not-exist",
                KerbalName = "Ghost Kerman",
                KerbalRole = "Pilot",
                KerbalEndStateField = KerbalEndState.Dead,
                Sequence = 1
            });

            var kerbals = Walk(new KerbalsModule(), actions);

            Assert.True(kerbals.Reservations.ContainsKey("Jeb"));
            Assert.False(kerbals.Reservations.ContainsKey("Ghost Kerman"));
            Assert.True(kerbals.IsKerbalAvailable("Ghost Kerman"));
        }

        // ========================================================
        // Idempotence
        // ========================================================

        [Fact]
        public void RepeatedWalks_TheWholeEndStateSurfaceIsStable()
        {
            // One recording per population so a drift in any branch shows up.
            AddFlight("idem-dead", new[] { "Jeb" }, new[] { "Jeb" },
                TerminalState.Destroyed, 100, 900);
            AddFlight("idem-recovered", new[] { "Val" }, new[] { "Val" },
                TerminalState.Recovered, 100, 2000);
            AddFlight("idem-unknown", new[] { "Bob" }, new[] { "Bill" },
                TerminalState.Docked, 100, 2000);
            AddFlight("idem-aboard", new[] { "Bill" }, new[] { "Bill" },
                TerminalState.Orbiting, 100, 2000);

            var module = new KerbalsModule();
            var roster = new FakeRoster();
            string firstFingerprint = null;

            for (int i = 0; i < 5; i++)
            {
                WalkStore(module);
                module.ApplyToRoster(roster);

                string fingerprint = Fingerprint(module);
                if (firstFingerprint == null)
                    firstFingerprint = fingerprint;

                Assert.Equal(firstFingerprint, fingerprint);
            }

            // Sanity: the fingerprint actually carries all four identities.
            foreach (var name in new[] { "Jeb", "Val", "Bob", "Bill" })
                Assert.Contains(name, firstFingerprint);
        }

        [Fact]
        public void RepeatedWalksWithApplyToRoster_DoNotDeepenTheChainOrRegenerateTheStandIn()
        {
            AddFlight("idem-standin", new[] { "Jeb" }, new[] { "Jeb" },
                TerminalState.Recovered, 100, 2000);

            var module = new KerbalsModule();
            var roster = new FakeRoster();

            WalkStore(module);
            module.ApplyToRoster(roster);
            string standIn = module.Slots["Jeb"].Chain[0];
            int createdAfterFirst = roster.CreatedCount;

            for (int i = 0; i < 4; i++)
            {
                WalkStore(module);
                module.ApplyToRoster(roster);
            }

            Assert.Single(module.Slots["Jeb"].Chain);
            Assert.Equal(standIn, module.Slots["Jeb"].Chain[0]);
            Assert.Equal(createdAfterFirst, roster.CreatedCount);
            Assert.Equal(0, roster.RecreatedCount);
        }

        private static string Fingerprint(KerbalsModule module)
        {
            var sb = new System.Text.StringBuilder();
            sb.Append("res:");
            foreach (var name in module.Reservations.Keys.OrderBy(k => k, StringComparer.Ordinal))
            {
                var r = module.Reservations[name];
                sb.Append(name).Append('=')
                  .Append(r.ReservedUntilUT.ToString("R", System.Globalization.CultureInfo.InvariantCulture))
                  .Append('/').Append(r.IsPermanent).Append(';');
            }
            sb.Append("|slots:");
            foreach (var owner in module.Slots.Keys.OrderBy(k => k, StringComparer.Ordinal))
            {
                var slot = module.Slots[owner];
                sb.Append(owner).Append('[').Append(slot.OwnerTrait).Append(',')
                  .Append(slot.OwnerPermanentlyGone).Append(']').Append('=')
                  .Append(string.Join(">", slot.Chain.Select(c => c ?? "<null>").ToArray()))
                  .Append(';');
            }
            sb.Append("|retired:");
            foreach (var name in module.RetiredKerbals.OrderBy(k => k, StringComparer.Ordinal))
                sb.Append(name).Append(';');
            sb.Append("|repl:");
            foreach (var kvp in CrewReservationManager.CrewReplacements
                         .OrderBy(k => k.Key, StringComparer.Ordinal))
                sb.Append(kvp.Key).Append("->").Append(kvp.Value).Append(';');
            return sb.ToString();
        }

        // ========================================================
        // Slot persistence summary
        // ========================================================

        [Fact]
        public void LoadSlots_ReportsLoadedAndIgnoredEntryCounts()
        {
            var module = new KerbalsModule();
            var parent = new ConfigNode("TEST");
            var slotsNode = parent.AddNode("KERBAL_SLOTS");

            var good = slotsNode.AddNode("SLOT");
            good.AddValue("owner", "Jeb");
            good.AddValue("trait", "Pilot");
            good.AddNode("CHAIN_ENTRY").AddValue("name", "Hanley Kerman");
            good.AddNode("CHAIN_ENTRY").AddValue("name", ""); // ignored

            var ownerless = slotsNode.AddNode("SLOT"); // ignored whole slot
            ownerless.AddValue("trait", "Pilot");

            var summary = module.LoadSlots(parent);

            Assert.True(summary.HasData);
            Assert.False(summary.LoadedFromLegacyCrewReplacements);
            Assert.Equal(1, summary.SlotsLoaded);
            Assert.Equal(1, summary.ChainEntriesLoaded);
            Assert.Equal(2, summary.IgnoredEntries);
        }

        [Fact]
        public void LoadSlots_NoNodeAtAll_ReportsNoData()
        {
            var module = new KerbalsModule();
            var summary = module.LoadSlots(new ConfigNode("TEST"));

            Assert.False(summary.HasData);
            Assert.Equal(0, summary.SlotsLoaded);
            Assert.Empty(module.Slots);
        }

        [Fact]
        public void LoadSlots_LegacyCrewReplacements_FlagsTheMigrationPath()
        {
            var module = new KerbalsModule();
            var parent = new ConfigNode("TEST");
            var legacy = parent.AddNode("CREW_REPLACEMENTS");
            var entry = legacy.AddNode("ENTRY");
            entry.AddValue("original", "Jeb");
            entry.AddValue("replacement", "Hanley Kerman");
            var broken = legacy.AddNode("ENTRY");
            broken.AddValue("original", "Val"); // no replacement -> ignored

            var summary = module.LoadSlots(parent);

            Assert.True(summary.HasData);
            Assert.True(summary.LoadedFromLegacyCrewReplacements);
            Assert.Equal(1, summary.SlotsLoaded);
            Assert.Equal(1, summary.IgnoredEntries);
            Assert.Equal("Hanley Kerman", module.Slots["Jeb"].Chain[0]);
        }

        // ========================================================
        // Fakes
        // ========================================================

        private sealed class FakeRoster : KerbalsModule.IKerbalRosterFacade
        {
            private readonly Dictionary<string, ProtoCrewMember.RosterStatus> statuses =
                new Dictionary<string, ProtoCrewMember.RosterStatus>();
            private int generatedSeq;

            internal int CreatedCount { get; private set; }
            internal int RecreatedCount { get; private set; }
            internal int RemovedCount { get; private set; }

            internal void Add(string name, ProtoCrewMember.RosterStatus status)
            {
                statuses[name] = status;
            }

            internal bool Contains(string name)
            {
                return statuses.ContainsKey(name);
            }

            internal ProtoCrewMember.RosterStatus StatusOf(string name)
            {
                return statuses[name];
            }

            public bool TryGetStatus(string name, out ProtoCrewMember.RosterStatus status)
            {
                return statuses.TryGetValue(name, out status);
            }

            public bool TryCreateGeneratedStandIn(string trait, out string generatedName)
            {
                generatedSeq++;
                generatedName = "StandIn " + generatedSeq + " Kerman";
                statuses[generatedName] = ProtoCrewMember.RosterStatus.Available;
                CreatedCount++;
                return true;
            }

            public bool TryRecreateStandIn(string desiredName, string trait)
            {
                statuses[desiredName] = ProtoCrewMember.RosterStatus.Available;
                RecreatedCount++;
                return true;
            }

            public bool TryRemove(string name)
            {
                if (!statuses.Remove(name)) return false;
                RemovedCount++;
                return true;
            }

            public bool IsKerbalOnLiveVessel(string kerbalName)
            {
                return false;
            }

            public bool IsKerbalOnVesselWithPid(string kerbalName, ulong vesselPersistentId)
            {
                return false;
            }
        }
    }
}
