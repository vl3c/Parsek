using System;
using System.Collections.Generic;
using Xunit;

namespace Parsek.Tests
{
    /// <summary>
    /// Phase 9 of Rewind-to-Staging (design §6.6 step 6 / §7.16 / §10.4):
    /// guards <see cref="CrewReservationManager.RecomputeAfterTombstones"/>.
    ///
    /// <para>
    /// After <see cref="SupersedeCommit.CommitTombstones"/> appends new
    /// <see cref="LedgerTombstone"/>s, the reservation walker must re-derive
    /// so kerbals whose death was just tombstoned leave the reservation
    /// dictionary (i.e. return to active) while surviving assignments stay
    /// reserved.
    /// </para>
    /// </summary>
    [Collection("Sequential")]
    public class CrewReservationRecomputeTests : IDisposable
    {
        private readonly List<string> logLines = new List<string>();
        private readonly bool priorParsekLogSuppress;
        private readonly bool priorStoreSuppress;
        private KerbalsModule priorKerbalsModule;

        public CrewReservationRecomputeTests()
        {
            priorParsekLogSuppress = ParsekLog.SuppressLogging;
            priorStoreSuppress = RecordingStore.SuppressLogging;
            priorKerbalsModule = LedgerOrchestrator.Kerbals;

            ParsekLog.ResetTestOverrides();
            ParsekLog.SuppressLogging = false;
            RecordingStore.SuppressLogging = true;
            ParsekLog.VerboseOverrideForTesting = true;
            ParsekLog.TestSinkForTesting = line => logLines.Add(line);

            RecordingStore.ResetForTesting();
            Ledger.ResetForTesting();
            EffectiveState.ResetCachesForTesting();
            ParsekScenario.ResetInstanceForTesting();
            SessionSuppressionState.ResetForTesting();
            LedgerOrchestrator.ResetForTesting();
            CrewReservationManager.ResetReplacementsForTesting();
        }

        public void Dispose()
        {
            ParsekLog.ResetTestOverrides();
            ParsekLog.SuppressLogging = priorParsekLogSuppress;
            RecordingStore.SuppressLogging = priorStoreSuppress;
            RecordingStore.ResetForTesting();
            Ledger.ResetForTesting();
            EffectiveState.ResetCachesForTesting();
            ParsekScenario.ResetInstanceForTesting();
            SessionSuppressionState.ResetForTesting();
            LedgerOrchestrator.ResetForTesting();
            LedgerOrchestrator.SetKerbalsForTesting(priorKerbalsModule);
            CrewReservationManager.ResetReplacementsForTesting();
        }

        // ---------- Fixture helpers ----------------------------------------

        private static Recording MakeRecording(string id, string treeId,
            string[] crew, double endUT = 200.0)
        {
            var snapshot = new ConfigNode("VESSEL");
            var part = snapshot.AddNode("PART");
            foreach (var c in crew)
                part.AddValue("crew", c);

            return new Recording
            {
                RecordingId = id,
                VesselName = id,
                TreeId = treeId,
                MergeState = MergeState.Immutable,
                VesselSnapshot = snapshot,
                GhostVisualSnapshot = snapshot,
                ExplicitStartUT = 0.0,
                ExplicitEndUT = endUT,
                LoopPlayback = false,
            };
        }

        private static GameAction KerbalAssignmentAction(
            string recordingId, string kerbalName, KerbalEndState endState,
            double ut, double endUT = 200.0)
        {
            return new GameAction
            {
                ActionId = "act_" + Guid.NewGuid().ToString("N"),
                Type = GameActionType.KerbalAssignment,
                RecordingId = recordingId,
                KerbalName = kerbalName,
                KerbalRole = "Pilot",
                StartUT = (float)ut,
                EndUT = (float)endUT,
                KerbalEndStateField = endState,
                UT = ut,
            };
        }

        private static ParsekScenario InstallScenarioWithTombstones(
            params LedgerTombstone[] tombstones)
        {
            var scenario = new ParsekScenario
            {
                RecordingSupersedes = new List<RecordingSupersedeRelation>(),
                LedgerTombstones = new List<LedgerTombstone>(tombstones),
                RewindPoints = new List<RewindPoint>(),
            };
            ParsekScenario.SetInstanceForTesting(scenario);
            scenario.BumpTombstoneStateVersion();
            EffectiveState.ResetCachesForTesting();
            return scenario;
        }

        // ---------- Tests ---------------------------------------------------

        [Fact]
        public void RecomputeAfterTombstones_DeadKerbalReturnsActive()
        {
            // Bill is Dead in rec_1. Without tombstones, reservation is permanent.
            // After tombstoning his death action, RecomputeAfterTombstones replays
            // ELS (tombstone filtered) → his assignment disappears from the walk
            // and he's no longer in the reservation dict.
            var rec = MakeRecording("rec_1", "tree_1", new[] { "Bill", "Jeb" });
            RecordingStore.AddRecordingWithTreeForTesting(rec);

            var deathBill = KerbalAssignmentAction("rec_1", "Bill", KerbalEndState.Dead, 100.0);
            var aboardJeb = KerbalAssignmentAction("rec_1", "Jeb", KerbalEndState.Aboard, 100.0);
            Ledger.AddAction(deathBill);
            Ledger.AddAction(aboardJeb);

            // Install the module so LedgerOrchestrator.Kerbals resolves.
            var kerbals = new KerbalsModule();
            LedgerOrchestrator.SetKerbalsForTesting(kerbals);

            // Baseline: with no tombstones, both Bill (Dead) and Jeb (Aboard) are reserved.
            InstallScenarioWithTombstones(/* empty */);
            CrewReservationManager.RecomputeAfterTombstones();
            Assert.True(kerbals.Reservations.ContainsKey("Bill"),
                "Before tombstones: Bill must be reserved (Dead)");
            Assert.True(kerbals.Reservations.ContainsKey("Jeb"),
                "Before tombstones: Jeb must be reserved (Aboard)");
            Assert.True(kerbals.Reservations["Bill"].IsPermanent,
                "Before tombstones: Bill's reservation is permanent (Dead)");

            // Tombstone Bill's death.
            var scenarioAfter = InstallScenarioWithTombstones(new LedgerTombstone
            {
                TombstoneId = "tomb_1",
                ActionId = deathBill.ActionId,
                RetiringRecordingId = "rec_provisional",
                UT = 150.0,
                CreatedRealTime = DateTime.UtcNow.ToString("o"),
            });

            CrewReservationManager.RecomputeAfterTombstones();

            // After tombstone + recompute: Bill is no longer reserved (his only
            // assignment action was tombstoned); Jeb's reservation survives.
            Assert.False(kerbals.Reservations.ContainsKey("Bill"),
                "After tombstones: Bill must NOT be reserved (his death was retired)");
            Assert.True(kerbals.Reservations.ContainsKey("Jeb"),
                "After tombstones: Jeb must still be reserved (his Aboard action stays in ELS)");
        }

        [Fact]
        public void RecomputeAfterTombstones_LogsCount()
        {
            var rec = MakeRecording("rec_1", "tree_1", new[] { "Jeb" });
            RecordingStore.AddRecordingWithTreeForTesting(rec);

            var aboard = KerbalAssignmentAction("rec_1", "Jeb", KerbalEndState.Aboard, 100.0);
            Ledger.AddAction(aboard);

            var kerbals = new KerbalsModule();
            LedgerOrchestrator.SetKerbalsForTesting(kerbals);

            InstallScenarioWithTombstones();
            logLines.Clear();

            CrewReservationManager.RecomputeAfterTombstones();

            Assert.Contains(logLines, l =>
                l.Contains("[CrewReservations]") &&
                l.Contains("Recomputed after tombstones: 1 reservations remain."));
        }

        [Fact]
        public void RecomputeAfterTombstones_NoTombstones_NoChange()
        {
            // With no tombstones the ELS = raw ledger; reservation output identical
            // to the default recalculation.
            var rec = MakeRecording("rec_1", "tree_1", new[] { "Bill" });
            RecordingStore.AddRecordingWithTreeForTesting(rec);

            var death = KerbalAssignmentAction("rec_1", "Bill", KerbalEndState.Dead, 100.0);
            Ledger.AddAction(death);

            var kerbals = new KerbalsModule();
            LedgerOrchestrator.SetKerbalsForTesting(kerbals);

            InstallScenarioWithTombstones();

            CrewReservationManager.RecomputeAfterTombstones();

            Assert.True(kerbals.Reservations.ContainsKey("Bill"));
            Assert.True(kerbals.Reservations["Bill"].IsPermanent);
        }

        [Fact]
        public void RecomputeAfterTombstones_NoKerbalsModule_NoOp()
        {
            // Safe-no-op path when LedgerOrchestrator has no kerbals module wired
            // (early boot / test fixture).
            LedgerOrchestrator.SetKerbalsForTesting(null);
            InstallScenarioWithTombstones();
            logLines.Clear();

            // Must not throw.
            CrewReservationManager.RecomputeAfterTombstones();

            Assert.Contains(logLines, l =>
                l.Contains("[CrewReservations]") &&
                l.Contains("no KerbalsModule"));
        }
    }
}
