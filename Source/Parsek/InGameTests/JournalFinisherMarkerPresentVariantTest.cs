using System.Collections.Generic;
using UnityEngine;

namespace Parsek.InGameTests
{
    /// <summary>
    /// Phase 10 of Rewind-to-Staging (design §6.9 step 2, §11.5):
    /// complements <see cref="MergeInterruptionRecoveryTest"/> by verifying
    /// the finisher drives completion cleanly when the journal arrives
    /// already in a post-Durable-1 phase (Durable1Done / RpReap /
    /// MarkerCleared / Durable2Done) WITH the marker still present on
    /// scenario, simulating a crash between Durable Save #1 and the marker
    /// clear in step 7. This is the "finisher marker-present completion
    /// variant" from the plan.
    ///
    /// <para>
    /// Preconditions: an active re-fly session marker and a provisional
    /// whose MergeState is already flipped (Immutable or
    /// CommittedProvisional) but a MergeJournal with Phase=Durable1Done
    /// is present. The test synthesizes this state directly on the live
    /// scenario (the production path creates it naturally when a
    /// post-Durable1 crash happens before the marker clear).
    /// </para>
    /// </summary>
    public class JournalFinisherMarkerPresentVariantTest
    {
        [InGameTest(Category = "Rewind", Scene = GameScenes.FLIGHT,
            Description = "Journal finisher: post-Durable1 phase with marker still present -> clean completion")]
        public void JournalFinisherMarkerPresentVariant()
        {
            var scenario = ParsekScenario.Instance;
            InGameAssert.IsNotNull(scenario, "ParsekScenario.Instance is null");

            var marker = scenario.ActiveReFlySessionMarker;
            if (marker == null)
            {
                InGameAssert.Skip(
                    "No active re-fly session marker. Invoke a rewind before running this test.");
                return;
            }

            // Preserve any real journal so this synthesis doesn't clobber
            // in-flight recovery state.
            var priorJournal = scenario.ActiveMergeJournal;

            // AND the marker, which this test CONSUMES. Running the finisher on a
            // post-Durable1 journal clears the live session marker - that is the
            // behaviour asserted below, not a side effect.
            //
            // FOUND 2026-08-04 by the R7b spec, which arms a real session with
            // InvokeRewind and then runs this whole category inside it. This test
            // ate the session and NINE later marker-dependent members - the Merge*
            // family, KerbalRecoveryOnSupersede, KerbalDualResidenceCarveOut - then
            // skipped on "No active re-fly session", having been reachable a moment
            // earlier (ContractTombstonesAcrossSupersede and InvokeRPStripAndActivate
            // both ran ahead of it and saw the marker). Every sibling in this family
            // already save-and-restores its scenario state in a finally; this one
            // had no try/finally at all.
            //
            // WHAT THE RESTORE IS AND IS NOT. Reinstating the marker exists so the
            // LATER marker-gated tests in the same batch are not starved - it does
            // NOT hand the borrowed session back undamaged. On a session-live path
            // the finisher's Durable1Done step also runs TagRpsForReap (permanently
            // promoting the real session's RP out of SessionProvisional),
            // ClearPreReFlyAnchorSnapshotsForSession, RenderSessionState.Clear and
            // ReFlyRevertButtonGate, and takes two durable saves that persist
            // marker=null. None of that is undone by putting the marker object back
            // on scenario. Treat a session this test has borrowed as spent.
            //
            // The restore runs AFTER the assertions, so nothing here is weakened:
            // the finisher still has to clear the marker for this test to pass.
            var priorMarker = marker;
            try
            {
                scenario.ActiveMergeJournal = new MergeJournal
                {
                    JournalId = "mj_intest_" + System.Guid.NewGuid().ToString("N"),
                    SessionId = marker.SessionId,
                    TreeId = marker.TreeId,
                    Phase = MergeJournal.Phases.Durable1Done,
                    StartedUT = Planetarium.GetUniversalTime(),
                    StartedRealTime = System.DateTime.UtcNow.ToString("o"),
                };

                ParsekLog.Info("RewindTest",
                    $"JournalFinisherMarkerPresentVariant: synthesized journal " +
                    $"sess={marker.SessionId ?? "<no-id>"} phase=Durable1Done; invoking finisher");

                bool finisherRan = MergeJournalOrchestrator.RunFinisher();
                InGameAssert.IsTrue(finisherRan, "Finisher did not run despite a synthesized journal");

                InGameAssert.IsNull(scenario.ActiveMergeJournal,
                    "After finisher completion the journal should be cleared");
                InGameAssert.IsNull(scenario.ActiveReFlySessionMarker,
                    "After finisher completion the marker should be cleared (post-Durable1 phase drives marker clear)");

                ParsekLog.Info("RewindTest",
                    $"JournalFinisherMarkerPresentVariant: finisher cleared marker + journal as expected");
            }
            finally
            {
                // Restore the journal we preempted. UNCONDITIONAL: when there was
                // no prior journal, priorJournal is null and assigning null IS the
                // correct restore. Guarding on `priorJournal != null` would leave
                // this test's synthetic `mj_intest_*` Durable1Done journal installed
                // on any failure path where the finisher did not clear it - the next
                // OnSave would persist it and the next OnLoad's RunFinisher would
                // drive it forward against the real session, outside any test.
                scenario.ActiveMergeJournal = priorJournal;
                scenario.ActiveReFlySessionMarker = priorMarker;
            }
        }
    }
}
