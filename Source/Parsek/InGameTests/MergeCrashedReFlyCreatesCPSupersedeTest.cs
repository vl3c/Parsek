using System.Collections.Generic;
using UnityEngine;

namespace Parsek.InGameTests
{
    /// <summary>
    /// Phase 8 of Rewind-to-Staging (design §6.6 step 2 / §7.17 / §7.43 /
    /// §11.5): end-to-end runtime test for the Crashed-terminal merge path.
    /// Runs in FLIGHT so the live <see cref="ParsekScenario"/> instance is
    /// available.
    ///
    /// <para>
    /// Preconditions: an active re-fly session marker + a provisional
    /// recording with a <see cref="TerminalState.Destroyed"/> terminal state
    /// (or another Crashed-kind state once BG-crash lands) must be present.
    /// The test auto-skips otherwise.
    /// </para>
    ///
    /// <para>
    /// Asserts after simulating the merge button:
    /// </para>
    /// <list type="bullet">
    ///   <item><description><c>provisional.MergeState == CommittedProvisional</c> (§6.6 step 2 — crashed re-fly stays re-flyable).</description></item>
    ///   <item><description>The origin subtree is covered by supersede relations pointing at the provisional.</description></item>
    ///   <item><description>The provisional remains visible in ERS (nothing supersedes it yet).</description></item>
    ///   <item><description>Active marker is null post-commit.</description></item>
    /// </list>
    /// </summary>
    public class MergeCrashedReFlyCreatesCPSupersedeTest
    {
        [InGameTest(Category = "Rewind", Scene = GameScenes.FLIGHT,
            Description = "Merge w/ Crashed terminal re-fly: provisional -> CommittedProvisional; chain still re-flyable")]
        public void MergeCrashedReFlyCreatesCPSupersede()
        {
            var scenario = ParsekScenario.Instance;
            InGameAssert.IsNotNull(scenario, "ParsekScenario.Instance is null");

            var marker = scenario.ActiveReFlySessionMarker;
            if (marker == null)
            {
                InGameAssert.Skip("No active re-fly session — invoke a rewind first, crash the re-fly, and rerun.");
                return;
            }

            string provisionalId = marker.ActiveReFlyRecordingId;
            if (string.IsNullOrEmpty(provisionalId))
            {
                InGameAssert.Skip("Marker has no ActiveReFlyRecordingId — cannot exercise Phase 8.");
                return;
            }

            Recording provisional = FindRecording(provisionalId);
            InGameAssert.IsNotNull(provisional, $"Provisional rec id={provisionalId} not found in committed list");

            var kind = TerminalKindClassifier.Classify(provisional);
            if (kind != TerminalKind.Crashed)
            {
                InGameAssert.Skip(
                    $"Provisional terminal kind is {kind}; this test requires Crashed (crash the re-fly and save before running).");
                return;
            }

            var subtreeBefore = EffectiveState.ComputeSessionSuppressedSubtree(marker);
            InGameAssert.IsTrue(subtreeBefore.Count > 0,
                "Expected at least one recording in the origin subtree closure before merge");
            int supersedeCountBefore = scenario.RecordingSupersedes?.Count ?? 0;

            ParsekLog.Info("RewindTest",
                $"MergeCrashedReFlyCreatesCPSupersede: sess={marker.SessionId} " +
                $"provisional={provisionalId} subtreeCount={subtreeBefore.Count} " +
                $"supersedesBefore={supersedeCountBefore}");

            SupersedeCommit.CommitSupersede(marker, provisional);

            InGameAssert.AreEqual(MergeState.CommittedProvisional, provisional.MergeState,
                $"Provisional MergeState should be CommittedProvisional after Crashed merge; got {provisional.MergeState}");
            InGameAssert.IsNull(scenario.ActiveReFlySessionMarker,
                "ActiveReFlySessionMarker should be cleared after commit");
            InGameAssert.IsNull(provisional.SupersedeTargetId,
                "provisional.SupersedeTargetId should be cleared after commit");

            int supersedeCountAfter = scenario.RecordingSupersedes.Count;
            int added = supersedeCountAfter - supersedeCountBefore;
            InGameAssert.AreEqual(subtreeBefore.Count, added,
                $"Expected {subtreeBefore.Count} new supersede relations; got {added}");

            // Provisional must still be visible (nothing supersedes it yet —
            // design §7.43 chain extension requires that the provisional
            // remain the slot's effective recording).
            bool provisionalVisible = EffectiveState.IsVisible(
                provisional, scenario.RecordingSupersedes);
            InGameAssert.IsTrue(provisionalVisible,
                "Provisional must be visible in ERS after commit (§7.43 chain extension)");

            ParsekLog.Info("RewindTest",
                $"MergeCrashedReFlyCreatesCPSupersede: all assertions passed " +
                $"(added {added} relations; provisional visible; MergeState=CommittedProvisional)");
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
