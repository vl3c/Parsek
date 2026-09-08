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
    /// provisional re-fly recording that the merge would accept as a
    /// supersede target. Auto-skips otherwise. The last clause is the sharp
    /// one: invoke a rewind, then land or crash the re-fly and save it, so
    /// the provisional carries both playable payload AND a terminal state.
    /// A re-fly that is merely airborne refuses on "null TerminalState".
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

            // PRECONDITION, not an assertion. The whole procedure below turns on
            // the merge actually writing supersede rows, and SupersedeCommit
            // REFUSES to supersede a target that is not a valid one:
            // `AppendRelations outcome=refused-unflown-provisional ... reason=null
            // TerminalState`, zero rows, merge completes with the origin still
            // effective. With zero rows the Durable1Done assertion below has an
            // UNMET PRECONDITION and reds against CORRECT product behaviour.
            //
            // FOUND 2026-09-08 by the RF-6 lane, the first committed lane to run
            // this category inside a LIVE re-fly session. The lane's provisional
            // carried points=1 playableSections=1 and still refused on `null
            // TerminalState`: the re-fly was airborne. Carrying Points is
            // NECESSARY AND NOT SUFFICIENT - the re-fly must be CONCLUDED (landed
            // or crashed, so a terminal state is stamped) and saved, not merely
            // flown. An earlier run of the same lane without the time jump refused
            // one clause earlier on `empty Points points=0`, which is how the two
            // clauses were told apart.
            //
            // The guard calls the PRODUCT predicate rather than approximating it,
            // exactly as KerbalRecoveryOnSupersedeTest's guard does, so it can
            // never drift from what the merge refuses on.
            string unconcludedSkip;
            if (TryBuildUnconcludedReFlySkip(provisional, out unconcludedSkip))
            {
                ParsekLog.Info("RewindTest",
                    $"MergeInterruptionRecovery: skipping - provisional={provisionalId} " +
                    $"is not a valid supersede target; {SupersedeCommit.DescribeSupersedePayload(provisional)}");
                InGameAssert.Skip(unconcludedSkip);
                return;
            }

            int supersedesBefore = scenario.RecordingSupersedes?.Count ?? 0;
            int tombstonesBefore = scenario.LedgerTombstones?.Count ?? 0;

            // TEARDOWN CONTRACT. Everything past this point installs a REAL
            // MergeJournal on the live scenario and deliberately crashes the
            // orchestrator inside it. Without an outer finally an assertion that
            // reds between the fault and RunFinisher() leaves
            // scenario.ActiveMergeJournal set for the rest of the batch, and every
            // later cell that touches a journal-active product guard fails as a
            // cascade victim: RF-6 lost three ReFlyRevertDialog cells that way
            // (RevertInterceptor's "refusing - merge journal active" and
            // ReFlyRevertDialog.BuildBody's journalActive branch), turning one
            // cell defect into failed=4. This is the same fix
            // JournalFinisherMarkerPresentVariantTest already carries.
            //
            // WHAT THE RESTORE IS AND IS NOT. Putting the marker object back keeps
            // the LATER marker-gated cells in the batch from starving; it does NOT
            // hand an undamaged session back. A merge that reached Durable1Done has
            // already written supersede rows, flipped MergeState and taken a
            // durable save. Treat a session this cell has borrowed as spent.
            var priorJournal = scenario.ActiveMergeJournal;
            var priorMarker = scenario.ActiveReFlySessionMarker;
            try
            {
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
            finally
            {
                // UNCONDITIONAL, like the sibling variant test: when there was no
                // prior journal, priorJournal is null and assigning null IS the
                // correct restore. Guarding on non-null would leave a live
                // Durable1Done journal installed on the very failure path this
                // teardown exists for, and the next OnSave would persist it.
                MergeJournalOrchestrator.FaultInjectionPoint = null;
                bool leakedJournal = scenario.ActiveMergeJournal != null;
                scenario.ActiveMergeJournal = priorJournal;
                scenario.ActiveReFlySessionMarker = priorMarker;
                ParsekLog.Info("RewindTest",
                    $"MergeInterruptionRecovery: teardown restored journal=" +
                    $"{(priorJournal != null ? priorJournal.JournalId ?? "<no-id>" : "none")} " +
                    $"marker={(priorMarker != null ? priorMarker.SessionId ?? "<no-id>" : "none")} " +
                    $"clearedLeakedJournal={leakedJournal} faultInjection=cleared");
            }
        }

        /// <summary>
        /// Pure precondition decision for the cell above: true when the merge
        /// would refuse to supersede <paramref name="provisional"/>, in which
        /// case <paramref name="skipMessage"/> carries the skip text naming the
        /// product's own refusal reason. False (with a null message) when the
        /// merge accepts the target and the procedure may run.
        /// Split out so it can be pinned headlessly in xUnit.
        /// </summary>
        internal static bool TryBuildUnconcludedReFlySkip(Recording provisional, out string skipMessage)
        {
            string refusal;
            if (SupersedeCommit.ValidateSupersedeTarget(provisional, out refusal))
            {
                skipMessage = null;
                return false;
            }

            string id = provisional != null
                ? (provisional.RecordingId ?? "<no-id>")
                : "<null>";
            skipMessage =
                "Provisional '" + id + "' is not a valid supersede target (" +
                (refusal ?? "<no-reason>") + "), so the merge writes 0 supersede rows " +
                "and the Durable1Done assertion has no subject. The re-fly must be " +
                "CONCLUDED - landed or crashed, so a terminal state is stamped - and " +
                "saved before running this test; merely flying it is not enough.";
            return true;
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
