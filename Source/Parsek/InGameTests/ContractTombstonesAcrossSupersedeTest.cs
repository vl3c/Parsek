using System.Collections.Generic;
using UnityEngine;

namespace Parsek.InGameTests
{
    /// <summary>
    /// End-to-end runtime test for broad contract tombstones across a re-fly
    /// merge. Runs in FLIGHT so the live <see cref="ParsekScenario"/>
    /// instance is available.
    ///
    /// <para>
    /// Preconditions: an active re-fly session marker + a provisional
    /// recording whose supersede subtree contains at least one
    /// <see cref="GameActionType.ContractComplete"/>,
    /// <see cref="GameActionType.ContractFail"/>,
    /// <see cref="GameActionType.ContractAccept"/>, or
    /// <see cref="GameActionType.ContractCancel"/> ledger action. The test
    /// auto-skips when no such contract lives in the subtree.
    /// </para>
    ///
    /// <para>
    /// Asserts after simulating the merge button:
    /// </para>
    /// <list type="bullet">
    ///   <item><description>Every pre-merge contract action from the subtree survives
    ///       in <see cref="Ledger.Actions"/> (nothing physically removed).</description></item>
    ///   <item><description>A <see cref="LedgerTombstone"/> was written against every
    ///       subtree contract action.</description></item>
    ///   <item><description>The contract action no longer appears in the Effective
    ///       Ledger Set (<see cref="EffectiveState.ComputeELS"/>).</description></item>
    /// </list>
    /// </summary>
    public class ContractTombstonesAcrossSupersedeTest
    {
        [InGameTest(Category = "Rewind", Scene = GameScenes.FLIGHT,
            Description = "Re-fly merge tombstones contract actions from the superseded subtree")]
        public void ContractTombstonesAcrossSupersede()
        {
            var scenario = ParsekScenario.Instance;
            InGameAssert.IsNotNull(scenario, "ParsekScenario.Instance is null");

            var marker = scenario.ActiveReFlySessionMarker;
            if (marker == null)
            {
                InGameAssert.Skip("No active re-fly session — invoke a rewind first, finish the re-fly, and rerun.");
                return;
            }

            string provisionalId = marker.ActiveReFlyRecordingId;
            if (string.IsNullOrEmpty(provisionalId))
            {
                InGameAssert.Skip("Marker has no ActiveReFlyRecordingId — cannot exercise Phase 9.");
                return;
            }

            Recording provisional = FindRecording(provisionalId);
            InGameAssert.IsNotNull(provisional,
                $"Provisional rec id={provisionalId} not found in committed list");

            // Collect every contract-type ledger action in the supersede subtree
            // BEFORE the merge commits. These are the items the test will check
            // for physical retention plus ELS tombstoning.
            var subtreeBefore = EffectiveState.ComputeSessionSuppressedSubtree(marker);
            var contractActionIds = new HashSet<string>();
            var subtreeSet = new HashSet<string>(subtreeBefore);
            foreach (var a in Ledger.Actions)
            {
                if (a == null) continue;
                if (string.IsNullOrEmpty(a.RecordingId)) continue;
                if (!subtreeSet.Contains(a.RecordingId)) continue;
                if (a.Type == GameActionType.ContractAccept
                    || a.Type == GameActionType.ContractComplete
                    || a.Type == GameActionType.ContractFail
                    || a.Type == GameActionType.ContractCancel)
                {
                    if (!string.IsNullOrEmpty(a.ActionId))
                        contractActionIds.Add(a.ActionId);
                }
            }

            if (contractActionIds.Count == 0)
            {
                InGameAssert.Skip(
                    "No contract actions in supersede subtree — accept/fail a contract in a BG-crash recording before running this test.");
                return;
            }

            ParsekLog.Info("RewindTest",
                $"ContractTombstonesAcrossSupersede: found {contractActionIds.Count} contract action(s) " +
                $"in subtree of size {subtreeBefore.Count}");

            // Simulate the merge commit. CommitSupersede runs CommitTombstones
            // internally.
            SupersedeCommit.CommitSupersede(marker, provisional);

            // Invariant 1: every contract action still physically exists in
            // Ledger.Actions (tombstoning does NOT remove; it only flags).
            var surviving = new HashSet<string>();
            foreach (var a in Ledger.Actions)
            {
                if (a == null || string.IsNullOrEmpty(a.ActionId)) continue;
                if (contractActionIds.Contains(a.ActionId))
                    surviving.Add(a.ActionId);
            }
            InGameAssert.AreEqual(contractActionIds.Count, surviving.Count,
                $"All contract actions must survive in Ledger.Actions; expected {contractActionIds.Count}, got {surviving.Count}");

            // Invariant 2: every subtree contract action has a tombstone.
            var tombs = scenario.LedgerTombstones ?? new List<LedgerTombstone>();
            var tombstoned = new HashSet<string>();
            foreach (var t in tombs)
            {
                if (t == null || string.IsNullOrEmpty(t.ActionId)) continue;
                if (contractActionIds.Contains(t.ActionId))
                    tombstoned.Add(t.ActionId);
            }
            InGameAssert.AreEqual(contractActionIds.Count, tombstoned.Count,
                $"All contract actions must be tombstoned; expected {contractActionIds.Count}, got {tombstoned.Count}");

            // Invariant 3: no subtree contract action remains in ELS.
            var els = EffectiveState.ComputeELS();
            var inEls = new HashSet<string>();
            foreach (var a in els)
            {
                if (a == null || string.IsNullOrEmpty(a.ActionId)) continue;
                if (contractActionIds.Contains(a.ActionId))
                    inEls.Add(a.ActionId);
            }
            InGameAssert.AreEqual(0, inEls.Count,
                $"No contract actions may remain in ELS after broad tombstone merge; got {inEls.Count}");

            ParsekLog.Info("RewindTest",
                $"ContractTombstonesAcrossSupersede: all {contractActionIds.Count} contract action(s) " +
                "survive physically in Ledger.Actions, are tombstoned, and are absent from ELS.");
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
