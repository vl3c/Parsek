using System;
using System.Collections.Generic;
using System.Globalization;
using Xunit;

namespace Parsek.Tests
{
    [Collection("Sequential")]
    public class KerbalEndStateTests : IDisposable
    {
        private readonly List<string> logLines = new List<string>();

        public KerbalEndStateTests()
        {
            RecordingStore.SuppressLogging = true;
            RecordingStore.ResetForTesting();
            ParsekLog.ResetTestOverrides();
            ParsekLog.SuppressLogging = false;
            ParsekLog.VerboseOverrideForTesting = true;
            ParsekLog.TestSinkForTesting = line => logLines.Add(line);
        }

        public void Dispose()
        {
            ParsekLog.ResetTestOverrides();
            ParsekLog.SuppressLogging = true;
            RecordingStore.SuppressLogging = true;
            RecordingStore.ResetForTesting();
        }

        #region InferCrewEndState — Core Logic

        /// <summary>
        /// When terminal state is null (recording still active or legacy), result is Unknown.
        /// Guards: legacy recordings with no terminal state don't crash.
        /// </summary>
        [Fact]
        public void InferCrewEndState_NullTerminalState_ReturnsUnknown()
        {
            var result = KerbalsModule.InferCrewEndState("Jeb", null, new HashSet<string> { "Jeb" });

            Assert.Equal(KerbalEndState.Unknown, result);
            Assert.Contains(logLines, l => l.Contains("[KerbalsModule]") && l.Contains("Unknown") && l.Contains("Jeb"));
        }

        /// <summary>
        /// Destroyed terminal state -> all crew are Dead regardless of snapshot.
        /// Guards: vessel destruction always kills all aboard crew.
        /// </summary>
        [Fact]
        public void InferCrewEndState_Destroyed_ReturnsDead()
        {
            var result = KerbalsModule.InferCrewEndState("Jeb", TerminalState.Destroyed, new HashSet<string> { "Jeb" });

            Assert.Equal(KerbalEndState.Dead, result);
            Assert.Contains(logLines, l => l.Contains("[KerbalsModule]") && l.Contains("Dead") && l.Contains("Jeb"));
        }

        /// <summary>
        /// Recovered terminal state -> all crew are Recovered regardless of snapshot.
        /// Guards: vessel recovery always recovers all aboard crew.
        /// </summary>
        [Fact]
        public void InferCrewEndState_Recovered_ReturnsRecovered()
        {
            var result = KerbalsModule.InferCrewEndState("Val", TerminalState.Recovered, null);

            Assert.Equal(KerbalEndState.Recovered, result);
            Assert.Contains(logLines, l => l.Contains("[KerbalsModule]") && l.Contains("Recovered") && l.Contains("Val"));
        }

        /// <summary>
        /// Intact terminal state (Orbiting) with crew in snapshot -> Aboard.
        /// Guards: crew still aboard an orbiting vessel are marked Aboard.
        /// </summary>
        [Fact]
        public void InferCrewEndState_OrbitingInSnapshot_ReturnsAboard()
        {
            var result = KerbalsModule.InferCrewEndState("Jeb", TerminalState.Orbiting, new HashSet<string> { "Jeb" });

            Assert.Equal(KerbalEndState.Aboard, result);
            Assert.Contains(logLines, l => l.Contains("[KerbalsModule]") && l.Contains("Aboard"));
        }

        /// <summary>
        /// Intact terminal state (Landed) with crew NOT in snapshot -> Dead (EVA'd and lost).
        /// Guards: crew missing from a landed vessel snapshot means they EVA'd and are presumed lost.
        /// </summary>
        [Fact]
        public void InferCrewEndState_LandedNotInSnapshot_ReturnsDead()
        {
            var result = KerbalsModule.InferCrewEndState("Bob", TerminalState.Landed, new HashSet<string> { "Jeb" });

            Assert.Equal(KerbalEndState.Dead, result);
            Assert.Contains(logLines, l => l.Contains("[KerbalsModule]") && l.Contains("Dead") && l.Contains("Bob"));
        }

        /// <summary>
        /// Boarded terminal state with crew in snapshot -> Aboard.
        /// Guards: crew still in vessel when it was boarded remain aboard.
        /// </summary>
        [Fact]
        public void InferCrewEndState_BoardedInSnapshot_ReturnsAboard()
        {
            var result = KerbalsModule.InferCrewEndState("Val", TerminalState.Boarded, new HashSet<string> { "Val" });

            Assert.Equal(KerbalEndState.Aboard, result);
            Assert.Contains(logLines, l => l.Contains("[KerbalsModule]") && l.Contains("Aboard"));
        }

        /// <summary>
        /// Docked terminal state with crew NOT in snapshot -> Unknown (transferred to other vessel).
        /// Guards: crew missing from a docked vessel means they transferred — unknown destination.
        /// </summary>
        [Fact]
        public void InferCrewEndState_DockedNotInSnapshot_ReturnsUnknown()
        {
            var result = KerbalsModule.InferCrewEndState("Bill", TerminalState.Docked, new HashSet<string>());

            Assert.Equal(KerbalEndState.Unknown, result);
            Assert.Contains(logLines, l => l.Contains("[KerbalsModule]") && l.Contains("Unknown") && l.Contains("Bill"));
        }

        /// <summary>
        /// Null snapshot crew set with intact terminal state -> Dead (no snapshot = EVA'd).
        /// Guards: null snapshotCrew is handled gracefully, treated as empty.
        /// </summary>
        [Fact]
        public void InferCrewEndState_NullSnapshotCrew_IntactState_ReturnsDead()
        {
            var result = KerbalsModule.InferCrewEndState("Jeb", TerminalState.Splashed, null);

            Assert.Equal(KerbalEndState.Dead, result);
        }

        #endregion

        #region PopulateCrewEndStates — Single Recording

        /// <summary>
        /// Populates end states for a recording with crew in both snapshots.
        /// Guards: end-to-end population path works with realistic data.
        /// </summary>
        [Fact]
        public void PopulateCrewEndStates_WithCrew_PopulatesDict()
        {
            var rec = new Recording
            {
                VesselName = "TestVessel",
                RecordingId = "test-001",
                TerminalStateValue = TerminalState.Orbiting
            };

            // Build ghost visual snapshot (recording-start crew)
            rec.GhostVisualSnapshot = BuildSnapshotWithCrew("Jeb", "Val");
            // Build vessel snapshot (recording-end crew — both still aboard)
            rec.VesselSnapshot = BuildSnapshotWithCrew("Jeb", "Val");

            KerbalsModule.PopulateCrewEndStates(rec);

            Assert.NotNull(rec.CrewEndStates);
            Assert.Equal(2, rec.CrewEndStates.Count);
            Assert.Equal(KerbalEndState.Aboard, rec.CrewEndStates["Jeb"]);
            Assert.Equal(KerbalEndState.Aboard, rec.CrewEndStates["Val"]);

            Assert.Contains(logLines, l => l.Contains("[KerbalsModule]") && l.Contains("aboard=2"));
        }

        /// <summary>
        /// No crew in ghost snapshot -> CrewEndStates stays null.
        /// Guards: crewless vessels (probes) don't get spurious end states.
        /// </summary>
        [Fact]
        public void PopulateCrewEndStates_NoCrew_StaysNull()
        {
            var rec = new Recording
            {
                VesselName = "Probe",
                RecordingId = "test-002",
                TerminalStateValue = TerminalState.Orbiting,
                GhostVisualSnapshot = new ConfigNode("VESSEL"),
                VesselSnapshot = new ConfigNode("VESSEL")
            };

            KerbalsModule.PopulateCrewEndStates(rec);

            Assert.Null(rec.CrewEndStates);
            Assert.Contains(logLines, l => l.Contains("[KerbalsModule]") && l.Contains("has no crew"));
        }

        /// <summary>
        /// Null recording -> no crash, just logs and skips.
        /// Guards: defensive null handling.
        /// </summary>
        [Fact]
        public void PopulateCrewEndStates_NullRecording_NoThrow()
        {
            KerbalsModule.PopulateCrewEndStates((Recording)null);

            Assert.Contains(logLines, l => l.Contains("[KerbalsModule]") && l.Contains("null recording"));
        }

        #endregion

        #region PopulateCrewEndStates — Batch Overload

        /// <summary>
        /// Batch populate skips recordings that already have CrewEndStates.
        /// Guards: re-running batch populate doesn't overwrite existing data.
        /// </summary>
        [Fact]
        public void PopulateCrewEndStates_Batch_SkipsAlreadyPopulated()
        {
            var alreadyPopulated = new Recording
            {
                VesselName = "Ship1",
                RecordingId = "batch-001",
                TerminalStateValue = TerminalState.Orbiting,
                GhostVisualSnapshot = BuildSnapshotWithCrew("Jeb"),
                VesselSnapshot = BuildSnapshotWithCrew("Jeb"),
                CrewEndStates = new Dictionary<string, KerbalEndState>
                {
                    { "Jeb", KerbalEndState.Aboard }
                }
            };

            var needsPopulation = new Recording
            {
                VesselName = "Ship2",
                RecordingId = "batch-002",
                TerminalStateValue = TerminalState.Destroyed,
                GhostVisualSnapshot = BuildSnapshotWithCrew("Val"),
                VesselSnapshot = null
            };

            var list = new List<Recording> { alreadyPopulated, needsPopulation };

            KerbalsModule.PopulateCrewEndStates(list);

            // First recording unchanged (already populated)
            Assert.Equal(KerbalEndState.Aboard, alreadyPopulated.CrewEndStates["Jeb"]);

            // Second recording now populated
            Assert.NotNull(needsPopulation.CrewEndStates);
            Assert.Equal(KerbalEndState.Dead, needsPopulation.CrewEndStates["Val"]);

            Assert.Contains(logLines, l => l.Contains("[KerbalsModule]") && l.Contains("populated=1") && l.Contains("skipped=1"));
        }

        #endregion

        #region Serialization Round-Trip

        /// <summary>
        /// CrewEndStates survives a save/load round-trip through ConfigNode serialization.
        /// Guards: data persists correctly across save/load cycles.
        /// </summary>
        [Fact]
        public void CrewEndStates_RoundTrip_PreservesData()
        {
            var rec = new Recording
            {
                RecordingId = "rt-001",
                CrewEndStates = new Dictionary<string, KerbalEndState>
                {
                    { "Jeb", KerbalEndState.Aboard },
                    { "Val", KerbalEndState.Dead },
                    { "Bob", KerbalEndState.Recovered },
                    { "Bill", KerbalEndState.Unknown }
                }
            };

            // Serialize
            var parentNode = new ConfigNode("TEST");
            RecordingStore.SerializeCrewEndStates(parentNode, rec);

            // Deserialize into a fresh recording
            var loaded = new Recording { RecordingId = "rt-001" };
            RecordingStore.DeserializeCrewEndStates(parentNode, loaded);

            Assert.NotNull(loaded.CrewEndStates);
            Assert.Equal(4, loaded.CrewEndStates.Count);
            Assert.Equal(KerbalEndState.Aboard, loaded.CrewEndStates["Jeb"]);
            Assert.Equal(KerbalEndState.Dead, loaded.CrewEndStates["Val"]);
            Assert.Equal(KerbalEndState.Recovered, loaded.CrewEndStates["Bob"]);
            Assert.Equal(KerbalEndState.Unknown, loaded.CrewEndStates["Bill"]);

            Assert.Contains(logLines, l => l.Contains("SerializeCrewEndStates") && l.Contains("wrote 4"));
            Assert.Contains(logLines, l => l.Contains("DeserializeCrewEndStates") && l.Contains("loaded=4"));
        }

        /// <summary>
        /// Missing CREW_END_STATES node -> CrewEndStates stays null (backward compat).
        /// Guards: legacy recordings without crew data don't crash on load.
        /// </summary>
        [Fact]
        public void DeserializeCrewEndStates_MissingNode_LeavesNull()
        {
            var rec = new Recording { RecordingId = "legacy-001" };
            var parentNode = new ConfigNode("TEST");

            RecordingStore.DeserializeCrewEndStates(parentNode, rec);

            Assert.Null(rec.CrewEndStates);
        }

        /// <summary>
        /// Null/empty CrewEndStates -> no CREW_END_STATES node written (compact save).
        /// Guards: recordings without crew data don't pollute the save file.
        /// </summary>
        [Fact]
        public void SerializeCrewEndStates_NullDict_WritesNothing()
        {
            var rec = new Recording { RecordingId = "empty-001", CrewEndStates = null };
            var parentNode = new ConfigNode("TEST");

            RecordingStore.SerializeCrewEndStates(parentNode, rec);

            Assert.Null(parentNode.GetNode("CREW_END_STATES"));
        }

        #endregion

        #region Helpers

        /// <summary>
        /// Builds a minimal vessel snapshot ConfigNode with the given crew names
        /// in a single PART node, mimicking CrewReservationManager.ExtractCrewFromSnapshot format.
        /// </summary>
        private static ConfigNode BuildSnapshotWithCrew(params string[] crewNames)
        {
            var vessel = new ConfigNode("VESSEL");
            var part = vessel.AddNode("PART");
            for (int i = 0; i < crewNames.Length; i++)
                part.AddValue("crew", crewNames[i]);
            return vessel;
        }

        #endregion

        #region Looping chain reservation guard

        [Fact]
        public void Recalculate_LoopingChain_RecoveredCrew_StaysInfinite()
        {
            // Setup: chain with 2 segments. Index 0 loops, index 1 is the tip with crew.
            RecordingStore.ResetForTesting();

            var loop = new Recording
            {
                RecordingId = "rec-loop",
                ChainId = "chain-A",
                ChainIndex = 0,
                LoopPlayback = true
            };
            loop.Points.Add(new TrajectoryPoint { ut = 10.0 });
            loop.Points.Add(new TrajectoryPoint { ut = 142.0 });
            RecordingStore.AddCommittedForTesting(loop);

            var tip = new Recording
            {
                RecordingId = "rec-tip",
                ChainId = "chain-A",
                ChainIndex = 1,
                VesselSnapshot = BuildSnapshotWithCrew("Jeb"),
                TerminalStateValue = TerminalState.Splashed
            };
            tip.Points.Add(new TrajectoryPoint { ut = 142.0 });
            tip.Points.Add(new TrajectoryPoint { ut = 200.0 });
            // Populate CrewEndStates (normally done by PopulateCrewEndStates)
            tip.CrewEndStates = new Dictionary<string, KerbalEndState>
            {
                { "Jeb", KerbalEndState.Recovered }
            };
            RecordingStore.AddCommittedForTesting(tip);

            var kerbals = KerbalsTestHelper.RecalculateFromStore();

            // Crew should be reserved with Infinity endUT because chain has a looping segment
            var reservations = kerbals.Reservations;
            Assert.True(reservations.ContainsKey("Jeb"),
                "Jeb should be reserved");
            Assert.True(double.IsPositiveInfinity(reservations["Jeb"].ReservedUntilUT),
                "endUT should be Infinity for looping chain despite Recovered endState");

            Assert.Contains(logLines, l =>
                l.Contains("[KerbalsModule]") && l.Contains("Reservation") && l.Contains("Jeb")
                && l.Contains("chainHasLoop"));

            RecordingStore.ResetForTesting();
        }

        [Fact]
        public void Recalculate_NonLoopingChain_RecoveredCrew_UsesFiniteEndUT()
        {
            // Same chain but no loop — crew should have finite endUT
            RecordingStore.ResetForTesting();

            var seg0 = new Recording
            {
                RecordingId = "rec-seg0",
                ChainId = "chain-B",
                ChainIndex = 0,
                LoopPlayback = false  // NOT looping
            };
            seg0.Points.Add(new TrajectoryPoint { ut = 10.0 });
            seg0.Points.Add(new TrajectoryPoint { ut = 142.0 });
            RecordingStore.AddCommittedForTesting(seg0);

            var tip = new Recording
            {
                RecordingId = "rec-tip-b",
                ChainId = "chain-B",
                ChainIndex = 1,
                VesselSnapshot = BuildSnapshotWithCrew("Val"),
                TerminalStateValue = TerminalState.Splashed
            };
            tip.Points.Add(new TrajectoryPoint { ut = 142.0 });
            tip.Points.Add(new TrajectoryPoint { ut = 200.0 });
            tip.CrewEndStates = new Dictionary<string, KerbalEndState>
            {
                { "Val", KerbalEndState.Recovered }
            };
            RecordingStore.AddCommittedForTesting(tip);

            var kerbals = KerbalsTestHelper.RecalculateFromStore();

            var reservations = kerbals.Reservations;
            Assert.True(reservations.ContainsKey("Val"));
            Assert.Equal(200.0, reservations["Val"].ReservedUntilUT);

            RecordingStore.ResetForTesting();
        }

        #endregion

        #region PopulateCrewEndStates — Stand-in Reverse-Map (#254)

        /// <summary>
        /// When a snapshot contains a stand-in name (e.g., Leia instead of Jeb),
        /// PopulateCrewEndStates should reverse-map it back to the original kerbal.
        /// Guards: prevents cascading crew replacement chains.
        /// </summary>
        [Fact]
        public void PopulateCrewEndStates_ReverseMapStandInNames()
        {
            // Set up: Jeb is reserved, Leia is his stand-in
            CrewReservationManager.SetReplacement("Jebediah Kerman", "Leia Kerman");

            // Build snapshots with Leia (the stand-in) as crew
            var ghostSnap = new ConfigNode("VESSEL");
            var part1 = ghostSnap.AddNode("PART");
            part1.AddValue("crew", "Leia Kerman");

            var vesselSnap = new ConfigNode("VESSEL");
            var part2 = vesselSnap.AddNode("PART");
            part2.AddValue("crew", "Leia Kerman");

            var rec = new Recording
            {
                VesselName = "Kerbal X",
                RecordingId = "test-reverse-map",
                TerminalStateValue = TerminalState.Landed,
                GhostVisualSnapshot = ghostSnap,
                VesselSnapshot = vesselSnap
            };

            KerbalsModule.PopulateCrewEndStates(rec);

            // Should have Jeb (original), not Leia (stand-in)
            Assert.NotNull(rec.CrewEndStates);
            Assert.True(rec.CrewEndStates.ContainsKey("Jebediah Kerman"),
                "CrewEndStates should contain original name 'Jebediah Kerman', not stand-in 'Leia Kerman'");
            Assert.False(rec.CrewEndStates.ContainsKey("Leia Kerman"),
                "CrewEndStates should NOT contain stand-in name 'Leia Kerman'");
            Assert.Equal(KerbalEndState.Aboard, rec.CrewEndStates["Jebediah Kerman"]);

            // Verify log mentions the reverse-map
            Assert.Contains(logLines, l => l.Contains("reverse-mapped") && l.Contains("Leia Kerman") && l.Contains("Jebediah Kerman"));

            CrewReservationManager.ResetReplacementsForTesting();
        }

        #endregion

        #region PopulateCrewEndStates — EVA fallback

        [Fact]
        public void PopulateCrewEndStates_EvaKerbalDestroyed_InfersDead()
        {
            var rec = new Recording
            {
                RecordingId = "eva-bill",
                VesselName = "Bill Kerman",
                EvaCrewName = "Bill Kerman",
                TerminalStateValue = TerminalState.Destroyed,
                // No crew in GhostVisualSnapshot (EVA ConfigNode has no PART/crew)
                GhostVisualSnapshot = new ConfigNode("VESSEL"),
            };

            KerbalsModule.PopulateCrewEndStates(rec);

            Assert.NotNull(rec.CrewEndStates);
            Assert.True(rec.CrewEndStates.ContainsKey("Bill Kerman"));
            Assert.Equal(KerbalEndState.Dead, rec.CrewEndStates["Bill Kerman"]);
            Assert.Contains(logLines, l => l.Contains("Bill Kerman") && l.Contains("Dead"));
        }

        [Fact]
        public void PopulateCrewEndStates_EvaKerbalLanded_InfersAboard()
        {
            var rec = new Recording
            {
                RecordingId = "eva-bill-landed",
                VesselName = "Bill Kerman",
                EvaCrewName = "Bill Kerman",
                TerminalStateValue = TerminalState.Landed,
                GhostVisualSnapshot = new ConfigNode("VESSEL"),
                // VesselSnapshot with the kerbal "still aboard" (in snapshot = aboard)
                VesselSnapshot = MakeSnapshotWithCrew("Bill Kerman"),
            };

            KerbalsModule.PopulateCrewEndStates(rec);

            Assert.NotNull(rec.CrewEndStates);
            Assert.True(rec.CrewEndStates.ContainsKey("Bill Kerman"));
            Assert.Equal(KerbalEndState.Aboard, rec.CrewEndStates["Bill Kerman"]);
        }

        [Fact]
        public void PopulateCrewEndStates_NoEvaCrewName_SkipsEmptyCrew()
        {
            var rec = new Recording
            {
                RecordingId = "no-eva",
                VesselName = "Mystery Ship",
                // No EvaCrewName, no crew in snapshot
                GhostVisualSnapshot = new ConfigNode("VESSEL"),
            };

            KerbalsModule.PopulateCrewEndStates(rec);

            Assert.Null(rec.CrewEndStates);
            Assert.Contains(logLines, l => l.Contains("no crew in ghost snapshot"));
        }

        private static ConfigNode MakeSnapshotWithCrew(params string[] crewNames)
        {
            var vessel = new ConfigNode("VESSEL");
            var part = new ConfigNode("PART");
            foreach (var name in crewNames)
                part.AddValue("crew", name);
            vessel.AddNode(part);
            return vessel;
        }

        #endregion
    }
}
