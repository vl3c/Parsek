using System.Collections.Generic;
using UnityEngine;

namespace Parsek.InGameTests
{
    /// <summary>
    /// Phase 10 of Rewind-to-Staging (design §6.6 failure-recovery matrix,
    /// §6.9 step 2, §11.5): live-scene verification that the
    /// <see cref="MergeJournalOrchestrator"/> finisher correctly completes
    /// an interrupted merge at the most interesting boundary — right after
    /// Durable Save #1 fires, when the supersedes + tombstones + MergeState
    /// flip are durable but the marker is still present and RPs are not yet
    /// reaped.
    ///
    /// <para>
    /// Preconditions: active re-fly session marker + a NotCommitted
    /// provisional re-fly recording. Auto-skips otherwise (invoke a rewind
    /// and let the re-fly land before running this test).
    /// </para>
    ///
    /// <para>
    /// Procedure:
    /// </para>
    /// <list type="number">
    ///   <item><description>Install <see cref="MergeJournalOrchestrator.FaultInjectionPoint"/> at Durable1Done.</description></item>
    ///   <item><description>Invoke <see cref="MergeJournalOrchestrator.RunMerge"/> and catch the fault.</description></item>
    ///   <item><description>Assert journal phase on disk = Durable1Done; marker still present.</description></item>
    ///   <item><description>Clear the fault injection + call <see cref="MergeJournalOrchestrator.RunFinisher"/>.</description></item>
    ///   <item><description>Assert journal cleared, marker cleared, MergeState flipped, supersedes present.</description></item>
    /// </list>
    /// </summary>
    public class MergeInterruptionRecoveryTest
    {
        [InGameTest(Category = "Rewind", Scene = GameScenes.FLIGHT,
            Description = "Merge interrupted at Durable1Done: finisher completes remaining steps on next OnLoad")]
        public void MergeInterruptionRecovery()
        {
            var scenario = ParsekScenario.Instance;
            InGameAssert.IsNotNull(scenario, "ParsekScenario.Instance is null");

            var marker = scenario.ActiveReFlySessionMarker;
            if (marker == null)
            {
                InGameAssert.Skip("No active re-fly session — invoke a rewind + crash/land the re-fly first.");
                return;
            }

            string provisionalId = marker.ActiveReFlyRecordingId;
            if (string.IsNullOrEmpty(provisionalId))
            {
                InGameAssert.Skip("Marker has no ActiveReFlyRecordingId — Phase 10 needs a provisional.");
                return;
            }

            Recording provisional = FindRecording(provisionalId);
            InGameAssert.IsNotNull(provisional,
                $"Provisional rec id={provisionalId} not found in committed list");

            if (provisional.MergeState != MergeState.NotCommitted)
            {
                InGameAssert.Skip(
                    $"Provisional MergeState is {provisional.MergeState}; test requires NotCommitted " +
                    "(run BEFORE any merge dialog).");
                return;
            }

            int supersedesBefore = scenario.RecordingSupersedes?.Count ?? 0;
            int tombstonesBefore = scenario.LedgerTombstones?.Count ?? 0;

            ParsekLog.Info("RewindTest",
                $"MergeInterruptionRecovery: preparing fault at Durable1Done " +
                $"sess={marker.SessionId ?? "<no-id>"} provisional={provisionalId}");

            MergeJournalOrchestrator.FaultInjectionPoint =
                MergeJournalOrchestrator.Phase.Durable1Done;

            bool faultCaught = false;
            try
            {
                MergeJournalOrchestrator.RunMerge(marker, provisional);
            }
            catch (MergeJournalOrchestrator.FaultInjectionException)
            {
                faultCaught = true;
            }
            finally
            {
                MergeJournalOrchestrator.FaultInjectionPoint = null;
            }

            InGameAssert.IsTrue(faultCaught,
                "Expected FaultInjectionException at Durable1Done; orchestrator completed normally instead.");

            InGameAssert.IsNotNull(scenario.ActiveMergeJournal,
                "After fault injection the MergeJournal must be on-scenario for the finisher to find it.");
            InGameAssert.AreEqual(MergeJournal.Phases.Durable1Done,
                scenario.ActiveMergeJournal.Phase,
                "Journal phase should be Durable1Done right after the fault.");
            InGameAssert.IsNotNull(scenario.ActiveReFlySessionMarker,
                "Marker must still be present at Durable1Done (marker clear happens in step 7).");

            int supersedesAtCrash = scenario.RecordingSupersedes.Count - supersedesBefore;
            InGameAssert.IsTrue(supersedesAtCrash > 0,
                $"Expected supersede relations to be durable at Durable1Done; got {supersedesAtCrash}");

            ParsekLog.Info("RewindTest",
                $"MergeInterruptionRecovery: fault captured; invoking finisher");

            bool finisherRan = MergeJournalOrchestrator.RunFinisher();
            InGameAssert.IsTrue(finisherRan, "Finisher did not run despite a live journal");

            InGameAssert.IsNull(scenario.ActiveMergeJournal,
                "After finisher completion the journal should be cleared");
            InGameAssert.IsNull(scenario.ActiveReFlySessionMarker,
                "After finisher completion the marker should be cleared");

            InGameAssert.IsTrue(
                provisional.MergeState == MergeState.Immutable
                || provisional.MergeState == MergeState.CommittedProvisional,
                $"Provisional MergeState should have flipped by now; got {provisional.MergeState}");

            ParsekLog.Info("RewindTest",
                $"MergeInterruptionRecovery: finisher drove the remaining steps; " +
                $"final provisional.MergeState={provisional.MergeState}");
        }

        private static Recording FindRecording(string recordingId)
        {
            if (string.IsNullOrEmpty(recordingId)) return null;
            var committed = RecordingStore.CommittedRecordings;
            if (committed == null) return null;
            for (int i = 0; i < committed.Count; i++)
            {
                var rec = committed[i];
                if (rec == null) continue;
                if (rec.RecordingId == recordingId) return rec;
            }
            return null;
        }
    }
}
