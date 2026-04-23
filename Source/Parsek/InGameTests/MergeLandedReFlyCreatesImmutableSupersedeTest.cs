using System.Collections.Generic;
using System.Linq;
using UnityEngine;

namespace Parsek.InGameTests
{
    /// <summary>
    /// Phase 8 of Rewind-to-Staging (design §6.6 step 2 / §11.5): end-to-end
    /// runtime test for the Landed-terminal merge path. Runs in FLIGHT so the
    /// live <see cref="ParsekScenario"/> instance is available.
    ///
    /// <para>
    /// Preconditions: an active re-fly session marker + a provisional
    /// recording with a Landed terminal state must be present. The test
    /// auto-skips when the preconditions aren't met — running this test
    /// outside a re-fly is meaningless.
    /// </para>
    ///
    /// <para>
    /// Asserts after simulating the merge button:
    /// </para>
    /// <list type="bullet">
    ///   <item><description><c>provisional.MergeState == Immutable</c></description></item>
    ///   <item><description>The origin subtree is covered by supersede relations pointing at the provisional</description></item>
    ///   <item><description>Active marker is null post-commit</description></item>
    ///   <item><description><c>SupersedeTargetId</c> is cleared</description></item>
    /// </list>
    /// </summary>
    public class MergeLandedReFlyCreatesImmutableSupersedeTest
    {
        [InGameTest(Category = "Rewind", Scene = GameScenes.FLIGHT,
            Description = "Merge w/ Landed terminal re-fly: provisional -> Immutable; subtree superseded")]
        public void MergeLandedReFlyCreatesImmutableSupersede()
        {
            var scenario = ParsekScenario.Instance;
            InGameAssert.IsNotNull(scenario, "ParsekScenario.Instance is null");

            var marker = scenario.ActiveReFlySessionMarker;
            if (marker == null)
            {
                InGameAssert.Skip("No active re-fly session — invoke a rewind first, land the re-fly, and rerun.");
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
            if (kind != TerminalKind.Landed)
            {
                InGameAssert.Skip(
                    $"Provisional terminal kind is {kind}; this test requires Landed (land safely and save before running).");
                return;
            }

            // Snapshot pre-state: origin subtree must be non-empty.
            var subtreeBefore = EffectiveState.ComputeSessionSuppressedSubtree(marker);
            InGameAssert.IsTrue(subtreeBefore.Count > 0,
                "Expected at least one recording in the origin subtree closure before merge");
            int supersedeCountBefore = scenario.RecordingSupersedes?.Count ?? 0;

            ParsekLog.Info("RewindTest",
                $"MergeLandedReFlyCreatesImmutableSupersede: sess={marker.SessionId} " +
                $"provisional={provisionalId} subtreeCount={subtreeBefore.Count} " +
                $"supersedesBefore={supersedeCountBefore}");

            // Simulate the merge button: invoke SupersedeCommit directly. In
            // production this runs inside MergeDialog.MergeCommit after
            // CommitPendingTree; in this test the tree-commit already happened
            // when the re-fly terminal state was captured, so we exercise the
            // Phase 8 commit step in isolation.
            SupersedeCommit.CommitSupersede(marker, provisional);

            // Assert the invariants.
            InGameAssert.AreEqual(MergeState.Immutable, provisional.MergeState,
                $"Provisional MergeState should be Immutable after Landed merge; got {provisional.MergeState}");
            InGameAssert.IsNull(scenario.ActiveReFlySessionMarker,
                "ActiveReFlySessionMarker should be cleared after commit");
            InGameAssert.IsNull(provisional.SupersedeTargetId,
                "provisional.SupersedeTargetId should be cleared after commit");

            int supersedeCountAfter = scenario.RecordingSupersedes.Count;
            int added = supersedeCountAfter - supersedeCountBefore;
            InGameAssert.AreEqual(subtreeBefore.Count, added,
                $"Expected {subtreeBefore.Count} new supersede relations; got {added}");

            // Every added relation must point at the provisional.
            for (int i = supersedeCountBefore; i < supersedeCountAfter; i++)
            {
                var rel = scenario.RecordingSupersedes[i];
                InGameAssert.AreEqual(provisionalId, rel.NewRecordingId,
                    $"Relation[{i}] NewRecordingId mismatch: expected {provisionalId}, got {rel.NewRecordingId}");
                InGameAssert.IsTrue(subtreeBefore.Contains(rel.OldRecordingId),
                    $"Relation[{i}] OldRecordingId {rel.OldRecordingId} must be in pre-commit subtree");
            }

            ParsekLog.Info("RewindTest",
                $"MergeLandedReFlyCreatesImmutableSupersede: all assertions passed " +
                $"(added {added} relations; final count={supersedeCountAfter})");
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
