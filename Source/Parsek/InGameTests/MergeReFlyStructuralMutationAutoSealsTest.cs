using System;
using UnityEngine;

namespace Parsek.InGameTests
{
    /// <summary>
    /// A Re-Fly that produced a structural side-off (decouple, stage,
    /// undock, joint break) during the session must auto-seal the slot on
    /// merge regardless of where the chain tip lands. Without this gate, a
    /// stashed-slot Re-Fly that staged debris and reached stable orbit
    /// would still classify as <c>stashedStableLeaf</c> and keep the slot
    /// re-flyable. With the structural-mutation rule, any session-tagged
    /// sibling Recording in a different chain from the provisional closes
    /// the slot.
    ///
    /// <para>
    /// Preconditions: an active Re-Fly session marker, the provisional
    /// recording's chain tip has a non-Crashed terminal (Crashed retains
    /// its existing retry-keep-open path), and at least one other
    /// Recording in the committed list shares the marker's
    /// <c>SessionId</c> via <c>CreatingSessionId</c> but sits in a
    /// different chain. The test auto-skips otherwise.
    /// </para>
    /// </summary>
    public class MergeReFlyStructuralMutationAutoSealsTest
    {
        [InGameTest(Category = "Rewind", Scene = GameScenes.FLIGHT,
            Description = "Re-Fly with structural mutation (decouple/stage/undock) auto-seals the slot")]
        public void MergeReFlyStructuralMutationAutoSeals()
        {
            var scenario = ParsekScenario.Instance;
            InGameAssert.IsNotNull(scenario, "ParsekScenario.Instance is null");

            var marker = scenario.ActiveReFlySessionMarker;
            if (marker == null || string.IsNullOrEmpty(marker.SessionId))
            {
                InGameAssert.Skip("No active re-fly session — invoke a rewind, decouple/stage/undock, and rerun.");
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

            Recording chainTip = EffectiveState.ResolveChainTerminalRecording(provisional);
            TerminalState? terminal = chainTip?.TerminalStateValue;
            if (terminal.HasValue && terminal.Value == TerminalState.Destroyed)
            {
                InGameAssert.Skip(
                    "Chain tip is Destroyed — structural-mutation rule defers to the existing crashed-keep-open path.");
                return;
            }

            string detail;
            if (!SupersedeCommit.HasReFlySessionStructuralMutation(provisional, marker, out detail))
            {
                InGameAssert.Skip(
                    "No session-tagged sibling recordings detected — decouple, stage, or undock during the Re-Fly to exercise this path.");
                return;
            }

            // Resolve slot before commit clears the marker so we can assert
            // post-commit slot state.
            RewindPoint rp;
            int slotListIndex;
            if (!UnfinishedFlightClassifier.TryResolveRewindPointForRecording(
                    provisional, out rp, out slotListIndex)
                || rp == null
                || rp.ChildSlots == null
                || slotListIndex < 0
                || slotListIndex >= rp.ChildSlots.Count)
            {
                InGameAssert.Skip("Could not resolve provisional's RP/slot.");
                return;
            }
            ChildSlot slotBefore = rp.ChildSlots[slotListIndex];
            InGameAssert.IsFalse(slotBefore.Sealed,
                "Slot must start un-sealed for this test to be meaningful.");

            ParsekLog.Info("RewindTest",
                $"MergeReFlyStructuralMutationAutoSeals: sess={marker.SessionId} " +
                $"provisional={provisionalId} slot={slotListIndex} " +
                $"terminal={(terminal.HasValue ? terminal.Value.ToString() : "<none>")} " +
                $"structural={detail}");

            SupersedeCommit.CommitSupersede(marker, provisional);

            InGameAssert.AreEqual(MergeState.Immutable, provisional.MergeState,
                $"Provisional MergeState should be Immutable; got {provisional.MergeState}");
            InGameAssert.IsTrue(slotBefore.Sealed,
                "Structural-mutation gate must auto-seal the slot.");
            InGameAssert.IsFalse(string.IsNullOrEmpty(slotBefore.SealedRealTime),
                "SealedRealTime should be stamped when the slot is auto-sealed.");
            InGameAssert.IsNull(scenario.ActiveReFlySessionMarker,
                "ActiveReFlySessionMarker should be cleared after commit.");

            ParsekLog.Info("RewindTest",
                "MergeReFlyStructuralMutationAutoSeals: all assertions passed " +
                $"(MergeState=Immutable; slot.Sealed=true; structural={detail})");
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
