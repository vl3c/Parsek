using UnityEngine;

namespace Parsek.InGameTests
{
    /// <summary>
    /// End-to-end pin for the "Merge &amp; Seal" button on a not-yet-sealable
    /// Re-Fly attempt (PR #938). That button commits the attempt through the
    /// normal merge journal and then closes the rewind slot now, reusing the
    /// Recordings-window Seal path
    /// (<see cref="UnfinishedFlightSealHandler.TrySeal"/>).
    ///
    /// <para>
    /// Reproduces the exact two-step that
    /// <c>MergeDialog.TryCommitReFlySupersede</c> runs when
    /// <c>playerRequestedSeal</c> is set:
    /// <see cref="MergeJournalOrchestrator.RunMerge"/> (which clears the
    /// active marker and reaps orphaned RewindPoints) followed by
    /// <see cref="MergeDialog.ApplyPlayerRequestedSeal"/>. It pins the two
    /// runtime-only claims the cloud review could not verify:
    /// </para>
    /// <list type="bullet">
    ///   <item><description>the still-re-flyable slot survives the journal's
    ///     RP reap, so <c>TrySeal</c> can still resolve it from the recording
    ///     after the marker has been cleared;</description></item>
    ///   <item><description>the seal closes the slot only
    ///     (<c>slot.Sealed == true</c>) and does NOT promote the recording to
    ///     <see cref="MergeState.Immutable"/> - it stays
    ///     <see cref="MergeState.CommittedProvisional"/>.</description></item>
    /// </list>
    ///
    /// <para>
    /// Preconditions: an active Re-Fly session marker on a slot whose merge
    /// outcome is NOT permanent (the dialog would offer the Merge &amp; Seal
    /// button). The test auto-skips otherwise. Drives the same production
    /// merge the Commit button runs, so run it in a scratch save.
    /// </para>
    /// </summary>
    public class MergeAndSealReFlyClosesSlotTest
    {
        [InGameTest(Category = "Rewind", Scene = GameScenes.FLIGHT,
            Description = "Merge & Seal on a not-yet-sealable Re-Fly: slot sealed, MergeState stays CommittedProvisional")]
        public void MergeAndSealReFlyClosesSlot()
        {
            var scenario = ParsekScenario.Instance;
            InGameAssert.IsNotNull(scenario, "ParsekScenario.Instance is null");

            var marker = scenario.ActiveReFlySessionMarker;
            if (marker == null)
            {
                InGameAssert.Skip("No active re-fly session - invoke a rewind, fly to a non-sealing outcome, and rerun.");
                return;
            }

            string provisionalId = marker.ActiveReFlyRecordingId;
            if (string.IsNullOrEmpty(provisionalId))
            {
                InGameAssert.Skip("Marker has no ActiveReFlyRecordingId.");
                return;
            }

            Recording provisional = FindRecording(provisionalId);
            InGameAssert.IsNotNull(provisional, $"Provisional rec id={provisionalId} not found in committed list");

            // Gate on the exact condition that makes the dialog show the third
            // button: a not-yet-permanent (keep-open) outcome. A permanent
            // outcome auto-seals and the Merge & Seal button is not offered, so
            // this test does not apply there.
            var preview = ReFlyAutoSealPreviewer.Preview(
                provisional, marker, FlightGlobals.ActiveVessel);
            string labelSource;
            bool permanent = MergeDialog.DetermineReFlyTimelineActionIsPermanent(
                provisional, marker, preview, out labelSource);
            if (permanent)
            {
                InGameAssert.Skip(
                    $"Re-Fly outcome is permanent (labelSource={labelSource}); the Merge & Seal button is only offered for not-yet-sealable attempts.");
                return;
            }

            RewindPoint rpBefore;
            int slotIndexBefore;
            if (!UnfinishedFlightClassifier.TryResolveRewindPointForRecording(
                    provisional, out rpBefore, out slotIndexBefore)
                || rpBefore?.ChildSlots == null
                || slotIndexBefore < 0
                || slotIndexBefore >= rpBefore.ChildSlots.Count)
            {
                InGameAssert.Skip("Could not resolve provisional's RP/slot before merge.");
                return;
            }
            var slotBefore = rpBefore.ChildSlots[slotIndexBefore];
            InGameAssert.IsNotNull(slotBefore, "Resolved slot is null before merge");
            InGameAssert.IsFalse(slotBefore.Sealed,
                "Slot must start unsealed - this test exercises closing an open slot.");

            ParsekLog.Info("RewindTest",
                $"MergeAndSealReFlyClosesSlot: sess={marker.SessionId} " +
                $"provisional={provisionalId} slot={slotIndexBefore} " +
                $"labelSource={labelSource}");

            // Step 1: the production merge journal - exactly what the Commit /
            // Merge & Seal buttons run via TryCommitReFlySupersede. It clears
            // the active marker and reaps orphaned RPs; a still-re-flyable slot
            // must survive that reap.
            bool merged = MergeJournalOrchestrator.RunMerge(marker, provisional);
            InGameAssert.IsTrue(merged, "MergeJournalOrchestrator.RunMerge returned false");
            InGameAssert.AreEqual(MergeState.CommittedProvisional, provisional.MergeState,
                $"Provisional MergeState should be CommittedProvisional after the keep-open merge; got {provisional.MergeState}");
            InGameAssert.IsNull(scenario.ActiveReFlySessionMarker,
                "ActiveReFlySessionMarker should be cleared after the merge journal completes.");

            // The slot must still resolve from the recording alone (the marker
            // is gone now) and must still be open before we seal.
            RewindPoint rpAfterMerge;
            int slotIndexAfterMerge;
            InGameAssert.IsTrue(
                UnfinishedFlightClassifier.TryResolveRewindPointForRecording(
                    provisional, out rpAfterMerge, out slotIndexAfterMerge)
                && rpAfterMerge?.ChildSlots != null
                && slotIndexAfterMerge >= 0
                && slotIndexAfterMerge < rpAfterMerge.ChildSlots.Count,
                "Slot must still resolve after the merge journal's RP reap (it is still re-flyable).");
            InGameAssert.IsFalse(rpAfterMerge.ChildSlots[slotIndexAfterMerge].Sealed,
                "Slot must still be open after the keep-open merge (the auto path does not seal it).");

            // Step 2: the new Merge & Seal post-merge step. After this the slot
            // must be sealed but the recording must NOT be promoted to
            // Immutable - seal closes the slot only.
            MergeDialog.ApplyPlayerRequestedSeal(provisional);

            RewindPoint rpAfterSeal;
            int slotIndexAfterSeal;
            InGameAssert.IsTrue(
                UnfinishedFlightClassifier.TryResolveRewindPointForRecording(
                    provisional, out rpAfterSeal, out slotIndexAfterSeal)
                && rpAfterSeal?.ChildSlots != null
                && slotIndexAfterSeal >= 0
                && slotIndexAfterSeal < rpAfterSeal.ChildSlots.Count,
                "Slot must still resolve after sealing.");
            InGameAssert.IsTrue(rpAfterSeal.ChildSlots[slotIndexAfterSeal].Sealed,
                "Merge & Seal must close the slot (slot.Sealed=true).");
            InGameAssert.AreEqual(MergeState.CommittedProvisional, provisional.MergeState,
                $"Merge & Seal must NOT promote the recording to Immutable; MergeState should stay CommittedProvisional, got {provisional.MergeState}");

            ParsekLog.Info("RewindTest",
                $"MergeAndSealReFlyClosesSlot: all assertions passed " +
                $"(slot {slotIndexAfterSeal} sealed; MergeState stays CommittedProvisional).");
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
