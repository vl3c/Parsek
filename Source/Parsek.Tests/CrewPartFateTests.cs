using System;
using System.Collections.Generic;
using System.Globalization;
using Xunit;

namespace Parsek.Tests
{
    /// <summary>
    /// Owner ruling Q6b (2026-11-21): on a breakup a kerbal dies only when KSP destroyed the
    /// PART he was in. A capsule that survives as its own piece keeps him alive with that
    /// piece (its recording decides), and a wreck that lost the recorded controllers while
    /// KSP still has it (identity loss) is not a death for the crew still sitting in it.
    /// </summary>
    [Collection("Sequential")]
    public class CrewPartFateTests : IDisposable
    {
        private const string Jeb = "Jebediah Kerman";
        private const string Bob = "Bob Kerman";
        private const uint PodPid = 1001;
        private const uint CabinPid = 2002;
        private const uint TankPid = 3003;
        private readonly List<string> logLines = new List<string>();

        public CrewPartFateTests()
        {
            ParsekLog.ResetTestOverrides();
            ParsekLog.SuppressLogging = false;
            ParsekLog.VerboseOverrideForTesting = true;
            ParsekLog.TestSinkForTesting = line => logLines.Add(line);
            RecordingStore.SuppressLogging = true;
            RecordingStore.ResetForTesting();
            KspStatePatcher.SuppressUnityCallsForTesting = true;
            GameStateStore.SuppressLogging = true;
            GameStateStore.ResetForTesting();
            LedgerOrchestrator.ResetForTesting();
            CrewReservationManager.ResetReplacementsForTesting();
            KerbalsModule.CrewRespawnPolicyProviderForTesting = null;
            KerbalsModule.EnsureTerminalStampObserverInstalled();
        }

        public void Dispose()
        {
            LedgerOrchestrator.ResetForTesting();
            KspStatePatcher.ResetForTesting();
            RecordingStore.ResetForTesting();
            RecordingStore.SuppressLogging = false;
            GameStateStore.ResetForTesting();
            CrewReservationManager.ResetReplacementsForTesting();
            KerbalsModule.CrewRespawnPolicyProviderForTesting = null;
            ParsekLog.ResetTestOverrides();
            ParsekLog.SuppressLogging = true;
        }

        // A snapshot whose PART nodes carry a persistentId and the named crew.
        private static ConfigNode Snapshot(params (uint pid, string[] crew)[] parts)
        {
            var snapshot = new ConfigNode("VESSEL");
            foreach (var (pid, crew) in parts)
            {
                var part = snapshot.AddNode("PART");
                part.AddValue("persistentId", pid.ToString(CultureInfo.InvariantCulture));
                foreach (string name in crew)
                    part.AddValue("crew", name);
            }
            return snapshot;
        }

        private static Recording Flight(string id, ConfigNode start, ConfigNode end)
        {
            return new Recording
            {
                RecordingId = id,
                VesselName = "Lander " + id,
                TreeId = "tree-" + id,
                VesselPersistentId = 4242,
                GhostVisualSnapshot = start,
                VesselSnapshot = end,
                ExplicitStartUT = 100.0,
                ExplicitEndUT = 300.0,
            };
        }

        private static void PartDied(Recording rec, uint pid, PartEventType type = PartEventType.Destroyed)
        {
            rec.PartEvents.Add(new PartEvent
            {
                ut = 200.0,
                partPersistentId = pid,
                eventType = type,
                partName = "part-" + pid.ToString(CultureInfo.InvariantCulture),
            });
        }

        // ---------------------------------------------------------------- the inference

        [Fact]
        public void InferCrewEndState_PredictedCrash_PartDestroyed_IsDead()
        {
            Assert.Equal(KerbalEndState.Dead, KerbalsModule.InferCrewEndState(
                Jeb, TerminalState.Destroyed, new HashSet<string> { Jeb },
                terminalPredicted: true, partDestroyed: true));
            Assert.Contains(logLines, l => l.Contains("[KerbalsModule]")
                && l.Contains("crew='Jebediah Kerman'") && l.Contains("part destroyed -> Dead"));
        }

        [Fact]
        public void InferCrewEndState_PredictedCrash_MissingFromTheEndSnapshot_IsDead()
        {
            // The vessel KSP still has does not carry him: his part died or he left it.
            Assert.Equal(KerbalEndState.Dead, KerbalsModule.InferCrewEndState(
                Jeb, TerminalState.Destroyed, new HashSet<string> { Bob }, terminalPredicted: true));
            // No end snapshot at all keeps the predicted answer.
            Assert.Equal(KerbalEndState.Aboard, KerbalsModule.InferCrewEndState(
                Jeb, TerminalState.Destroyed, null, terminalPredicted: true));
        }

        // ---------------------------------------------------------------- part deaths

        // catches: a predicted crash keeping Aboard a kerbal whose capsule KSP blew up
        // earlier in the flight (the vessel flew on without it).
        [Fact]
        public void Populate_PredictedCrash_KerbalInADestroyedPart_IsDead_OthersAboard()
        {
            var start = Snapshot((PodPid, new[] { Jeb }), (CabinPid, new[] { Bob }));
            Recording rec = Flight("rec-pd", start, null);
            PartDied(rec, CabinPid);
            rec.StampTerminalState(TerminalState.Destroyed, "test", predicted: true);

            KerbalsModule.PopulateCrewEndStates(rec);

            Assert.Equal(KerbalEndState.Aboard, rec.CrewEndStates[Jeb]);
            Assert.Equal(KerbalEndState.Dead, rec.CrewEndStates[Bob]);
            Assert.Contains(logLines, l => l.Contains("[KerbalsModule]")
                && l.Contains("crew='Bob Kerman'") && l.Contains("part=2002"));
        }

        [Fact]
        public void Populate_PredictedCrash_ParachuteDestroyedPart_IsAPartDeath()
        {
            var start = Snapshot((PodPid, new[] { Jeb }));
            Recording rec = Flight("rec-chute", start, null);
            PartDied(rec, PodPid, PartEventType.ParachuteDestroyed);
            rec.StampTerminalState(TerminalState.Destroyed, "test", predicted: true);

            KerbalsModule.PopulateCrewEndStates(rec);

            Assert.Equal(KerbalEndState.Dead, rec.CrewEndStates[Jeb]);
        }

        // catches: a part death read off the START seat of a kerbal who moved to another
        // part that survived (the end snapshot shows where he really was).
        [Fact]
        public void Populate_PredictedCrash_KerbalMovedToASurvivingPart_IsAboard()
        {
            var start = Snapshot((PodPid, new[] { Jeb }), (CabinPid, new string[0]));
            var end = Snapshot((CabinPid, new[] { Jeb }));
            Recording rec = Flight("rec-moved", start, end);
            PartDied(rec, PodPid);
            rec.StampTerminalState(TerminalState.Destroyed, "test", predicted: true);

            KerbalsModule.PopulateCrewEndStates(rec);

            Assert.Equal(KerbalEndState.Aboard, rec.CrewEndStates[Jeb]);
        }

        // An old recording (no predicted key) keeps its outcome: a Destroyed ending is Dead.
        [Fact]
        public void Populate_ConfirmedCrash_StaysDead_WithOrWithoutAPartDeath()
        {
            var start = Snapshot((PodPid, new[] { Jeb }), (CabinPid, new[] { Bob }));
            Recording rec = Flight("rec-conf", start, null);
            PartDied(rec, CabinPid);
            rec.StampTerminalState(TerminalState.Destroyed, "test");

            KerbalsModule.PopulateCrewEndStates(rec);

            Assert.Equal(KerbalEndState.Dead, rec.CrewEndStates[Jeb]);
            Assert.Equal(KerbalEndState.Dead, rec.CrewEndStates[Bob]);
        }

        // ---------------------------------------------------------------- identity loss

        // catches: a breakup remnant that kept a crewed cabin but none of the recorded
        // controllers (identity loss) killing the kerbal still sitting alive in the cabin.
        [Fact]
        public void IdentityLoss_RemnantWithTheCabin_KeepsItsKerbal_KillsTheOneWhosePodDied()
        {
            var start = Snapshot((PodPid, new[] { Jeb }), (CabinPid, new[] { Bob }), (TankPid, new string[0]));
            Recording rec = Flight("rec-idloss", start, null);
            PartDied(rec, PodPid);

            rec.MarkDestroyedAtTerminal(250.0, "test.identityLost", vesselSurvives: true);
            KerbalsModule.PopulateCrewEndStates(rec);

            Assert.Equal(TerminalState.Destroyed, rec.TerminalStateValue);
            Assert.True(rec.TerminalPredicted);
            Assert.Equal(KerbalEndState.Dead, rec.CrewEndStates[Jeb]);
            Assert.Equal(KerbalEndState.Aboard, rec.CrewEndStates[Bob]);
            Assert.Contains(logLines, l => l.Contains("[Recording]")
                && l.Contains("MarkDestroyedAtTerminal: rec=rec-idloss") && l.Contains("vesselSurvives=True"));
        }

        [Fact]
        public void MarkDestroyedAtTerminal_Default_IsAConfirmedDestruction()
        {
            Recording rec = Flight("rec-real", Snapshot((PodPid, new[] { Jeb })), null);
            rec.MarkDestroyedAtTerminal(250.0, "test.realDestruction");
            KerbalsModule.PopulateCrewEndStates(rec);

            Assert.False(rec.TerminalPredicted);
            Assert.Equal(KerbalEndState.Dead, rec.CrewEndStates[Jeb]);
        }

        // ---------------------------------------------------------------- breakup pieces

        private static RecordingTree BreakupTree(Recording parent, Recording child, BranchPointType type)
        {
            var tree = new RecordingTree
            {
                Id = parent.TreeId,
                TreeName = "Breakup",
                RootRecordingId = parent.RecordingId,
            };
            child.TreeId = parent.TreeId;
            child.ParentBranchPointId = "bp-breakup";
            parent.ChildBranchPointId = "bp-breakup";
            tree.BranchPoints.Add(new BranchPoint
            {
                Id = "bp-breakup",
                UT = 150.0,
                Type = type,
                ParentRecordingIds = new List<string> { parent.RecordingId },
                ChildRecordingIds = new List<string> { child.RecordingId },
            });
            tree.AddOrReplaceRecording(parent);
            tree.AddOrReplaceRecording(child);
            return tree;
        }

        // catches: a breakup whose parent end snapshot was never refreshed (it still lists the
        // kerbal in the pod the breakup child carried away) killing him with the wreck while
        // his pod flies on as its own recording.
        [Fact]
        public void Breakup_StaleParentSnapshot_KerbalInThePodTheChildTook_IsNotDead()
        {
            var start = Snapshot((PodPid, new[] { Jeb }), (TankPid, new string[0]));
            Recording parent = Flight("rec-wreck", start, start.CreateCopy());
            parent.StampTerminalState(TerminalState.Destroyed, "test");
            Recording pod = Flight("rec-pod", Snapshot((PodPid, new[] { Jeb })),
                Snapshot((PodPid, new[] { Jeb })));
            pod.ExplicitStartUT = 150.0;
            pod.StampTerminalState(TerminalState.Landed, "test");
            RecordingTree tree = BreakupTree(parent, pod, BranchPointType.Breakup);

            KerbalsModule.PopulateCrewEndStates(parent, tree);
            KerbalsModule.PopulateCrewEndStates(pod, tree);

            Assert.Equal(KerbalEndState.Unknown, parent.CrewEndStates[Jeb]);
            Assert.Equal(KerbalEndState.Aboard, pod.CrewEndStates[Jeb]);
            Assert.Contains(logLines, l => l.Contains("[KerbalsModule]")
                && l.Contains("crew='Jebediah Kerman'") && l.Contains("part=1001")
                && l.Contains("carried away by a breakup piece"));
        }

        // An undock child that docks back: the parent's end snapshot is authoritative there,
        // so only a breakup / joint break reads a carried part as a stale end seat.
        [Fact]
        public void Undock_ParentSnapshotListsTheKerbal_KeepsTheParentAnswer()
        {
            var start = Snapshot((PodPid, new[] { Jeb }), (TankPid, new string[0]));
            Recording parent = Flight("rec-station", start, start.CreateCopy());
            parent.StampTerminalState(TerminalState.Destroyed, "test");
            Recording pod = Flight("rec-pod2", Snapshot((PodPid, new[] { Jeb })), null);
            pod.ExplicitStartUT = 150.0;
            pod.StampTerminalState(TerminalState.Docked, "test");
            RecordingTree tree = BreakupTree(parent, pod, BranchPointType.Undock);

            KerbalsModule.PopulateCrewEndStates(parent, tree);

            Assert.Equal(KerbalEndState.Dead, parent.CrewEndStates[Jeb]);
        }

        // ---------------------------------------------------------------- the pure helpers

        [Fact]
        public void ExtractCrewPartPids_MapsEachKerbalToTheFirstPartListingHim()
        {
            var snapshot = Snapshot((PodPid, new[] { Jeb }), (CabinPid, new[] { Bob }));
            var map = KerbalsModule.ExtractCrewPartPids(
                snapshot, new Dictionary<string, string>());
            Assert.Equal(PodPid, map[Jeb]);
            Assert.Equal(CabinPid, map[Bob]);
            Assert.Empty(KerbalsModule.ExtractCrewPartPids(null, new Dictionary<string, string>()));
        }

        [Fact]
        public void ExtractCrewPartPids_ReverseMapsStandIns()
        {
            var snapshot = Snapshot((PodPid, new[] { "Leia Kerman" }));
            var map = KerbalsModule.ExtractCrewPartPids(
                snapshot, new Dictionary<string, string> { { Jeb, "Leia Kerman" } });
            Assert.Equal(PodPid, map[Jeb]);
            Assert.False(map.ContainsKey("Leia Kerman"));
        }

        [Fact]
        public void CollectDestroyedPartPids_ReadsDestroyedAndChuteDestroyedOnly()
        {
            var rec = new Recording();
            PartDied(rec, PodPid);
            PartDied(rec, CabinPid, PartEventType.ParachuteDestroyed);
            PartDied(rec, TankPid, PartEventType.Decoupled);
            var pids = KerbalsModule.CollectDestroyedPartPids(rec);
            Assert.Contains(PodPid, pids);
            Assert.Contains(CabinPid, pids);
            Assert.DoesNotContain(TankPid, pids);
        }

        // catches: a small crewed piece (an external seat, a lone cabin under the debris
        // thresholds) dropped as trivial debris, so the kerbal in it has no recording and reads
        // as lost on the parent.
        [Fact]
        public void ShouldRecordDebris_ACrewedPieceIsAlwaysRecorded()
        {
            Assert.False(ParsekFlight.ShouldRecordDebris(1, 0.05f, 0));
            Assert.True(ParsekFlight.ShouldRecordDebris(1, 0.05f, 1));
            Assert.True(ParsekFlight.ShouldRecordDebris(3, 0.05f, 0));
        }
    }
}
