using UnityEngine;

namespace Parsek.InGameTests
{
    /// <summary>
    /// Re-Fly target promotes focus for seal classification: when the player
    /// Re-Flies a non-focus slot (e.g. a side-debris probe) and reaches a
    /// stable Orbiting / SubOrbital terminal, the merge must commit
    /// <see cref="MergeState.Immutable"/>. Without the focus override, the
    /// slot-aware classifier would compare against the capture-time
    /// <c>rp.FocusSlotIndex</c> (the parent stage), classify the outcome as
    /// <c>stableLeafUnconcluded</c>, and keep the slot re-flyable — leaving
    /// the recording at <see cref="MergeState.CommittedProvisional"/>.
    ///
    /// <para>
    /// Preconditions: an active re-fly session marker on a slot whose index
    /// is <em>not</em> the rewind point's <c>FocusSlotIndex</c>, and the
    /// chain-tip terminal is Orbiting or SubOrbital. The test auto-skips
    /// otherwise — this scenario can only be set up by the player.
    /// </para>
    /// </summary>
    public class MergeNonFocusReFlyToOrbitImmutableTest
    {
        [InGameTest(Category = "Rewind", Scene = GameScenes.FLIGHT,
            Description = "Merge non-focus-slot re-fly to stable orbit: provisional -> Immutable (focus override)")]
        public void MergeNonFocusReFlyToOrbitImmutable()
        {
            var scenario = ParsekScenario.Instance;
            InGameAssert.IsNotNull(scenario, "ParsekScenario.Instance is null");

            var marker = scenario.ActiveReFlySessionMarker;
            if (marker == null)
            {
                InGameAssert.Skip("No active re-fly session — invoke a rewind on a non-focus slot first.");
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

            // Resolve the provisional's slot in its rewind point so we can
            // assert this is the non-focus path.
            RewindPoint rp;
            int slotListIndex;
            if (!UnfinishedFlightClassifier.TryResolveRewindPointForRecording(
                    provisional, out rp, out slotListIndex)
                || rp == null
                || slotListIndex < 0)
            {
                InGameAssert.Skip("Could not resolve provisional's RP/slot — cannot exercise focus-override path.");
                return;
            }
            if (rp.FocusSlotIndex < 0)
            {
                InGameAssert.Skip(
                    $"rp.FocusSlotIndex={rp.FocusSlotIndex} (no focus signal) — exercises noFocusSignalOrbiting branch instead.");
                return;
            }
            if (slotListIndex == rp.FocusSlotIndex)
            {
                InGameAssert.Skip(
                    $"slot={slotListIndex} == focusSlot={rp.FocusSlotIndex}; this test requires a non-focus re-fly target.");
                return;
            }

            Recording chainTip = EffectiveState.ResolveChainTerminalRecording(provisional);
            TerminalState? terminal = chainTip?.TerminalStateValue;
            if (!terminal.HasValue || terminal.Value != TerminalState.Orbiting)
            {
                // SubOrbital is excluded: under the post-fix contract a
                // suborbital arc is still in flight and the merge lands at
                // CommittedProvisional via stableLeafUnconcluded, not at
                // Immutable. This test pins the Orbiting path only;
                // the SubOrbital end-to-end behavior has its own test
                // (MergeReFlyToSubOrbitalKeepsSlotOpenTest).
                InGameAssert.Skip(
                    $"chain-tip terminal is {(terminal.HasValue ? terminal.Value.ToString() : "<none>")}; this test requires Orbiting.");
                return;
            }

            ParsekLog.Info("RewindTest",
                $"MergeNonFocusReFlyToOrbitImmutable: sess={marker.SessionId} " +
                $"provisional={provisionalId} slot={slotListIndex} " +
                $"focusSlot={rp.FocusSlotIndex} terminal={terminal.Value}");

            SupersedeCommit.CommitSupersede(marker, provisional);

            // The focus override must drive the classifier to
            // stableTerminalFocusSlot, closing the slot and producing
            // MergeState.Immutable. Without the override this would land at
            // CommittedProvisional via stableLeafUnconcluded.
            InGameAssert.AreEqual(MergeState.Immutable, provisional.MergeState,
                $"Provisional MergeState should be Immutable after non-focus re-fly to {terminal.Value}; got {provisional.MergeState}");
            InGameAssert.IsNull(scenario.ActiveReFlySessionMarker,
                "ActiveReFlySessionMarker should be cleared after commit");
            InGameAssert.IsNull(provisional.SupersedeTargetId,
                "provisional.SupersedeTargetId should be cleared after commit");

            ParsekLog.Info("RewindTest",
                "MergeNonFocusReFlyToOrbitImmutable: all assertions passed " +
                $"(MergeState=Immutable; slot {slotListIndex} promoted as focus over rp.FocusSlotIndex={rp.FocusSlotIndex})");
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
