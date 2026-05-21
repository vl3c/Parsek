using System;
using UnityEngine;

namespace Parsek.InGameTests
{
    /// <summary>
    /// In-game smoke test for <see cref="SupersedeCommit.HasReFlySessionStructuralMutation"/>.
    /// Under the v0.9.1 auto-seal revision (design §4.6) the focus
    /// override path closes the slot via <c>stableTerminalFocusSlot</c>
    /// before the structural gate runs for Landed / Splashed / Orbiting
    /// chain tips, so the structural gate is a defensive backstop for
    /// those terminals. For <see cref="TerminalState.SubOrbital"/> chain
    /// tips the override path no longer fires (a suborbital arc is still
    /// in flight, not a conclusion under the post-fix contract), so the
    /// structural gate is the PRIMARY (and only) seal trigger for that
    /// terminal; the merge still produces a fork (the slot's effective tip)
    /// at <see cref="MergeState.Immutable"/> via
    /// <c>structuralMutation:*</c>. This test exercises the seal outcome
    /// end-to-end on a stashed slot Re-Fly that produced a structural BP
    /// and reached a stable terminal — the merge succeeds (tip Immutable =
    /// slot closed), the user sees the row leave Unfinished Flights, and
    /// the verdict log carries either <c>classifierReason=stableTerminalFocusSlot</c>
    /// (override path, fires for Landed / Splashed / Orbiting) or
    /// <c>structuralMutation:*</c> (backstop for those terminals,
    /// primary for SubOrbital). Helper-level coverage for the gate's
    /// mechanics — rp.UT cutoff, <c>PreSessionBranchPointIds</c>
    /// baseline, lineage scope — lives in xUnit
    /// (<c>SupersedeCommitTests.HasReFlySessionStructuralMutation_*</c>).
    ///
    /// <para>
    /// Preconditions: an active Re-Fly session marker on a Stashed slot,
    /// the provisional's chain tip has a non-Crashed terminal (Crashed
    /// retains its existing retry-keep-open path), and the provisional's
    /// tree carries at least one structural branch point authored past
    /// the rewind-point UT. The test auto-skips otherwise.
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
            InGameAssert.IsNotNull(slotBefore, "Resolved slot is null");
            InGameAssert.AreNotEqual(MergeState.Immutable, provisional.MergeState,
                "Slot must start open (tip not yet Immutable) for this test to be meaningful.");

            ParsekLog.Info("RewindTest",
                $"MergeReFlyStructuralMutationAutoSeals: sess={marker.SessionId} " +
                $"provisional={provisionalId} slot={slotListIndex} " +
                $"terminal={(terminal.HasValue ? terminal.Value.ToString() : "<none>")} " +
                $"structural={detail}");

            SupersedeCommit.CommitSupersede(marker, provisional);

            InGameAssert.AreEqual(MergeState.Immutable, provisional.MergeState,
                $"Provisional (slot tip) MergeState should be Immutable (slot closed); got {provisional.MergeState}");
            InGameAssert.IsNull(scenario.ActiveReFlySessionMarker,
                "ActiveReFlySessionMarker should be cleared after commit.");

            ParsekLog.Info("RewindTest",
                "MergeReFlyStructuralMutationAutoSeals: all assertions passed " +
                $"(tip MergeState=Immutable = slot closed; structural={detail})");
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
