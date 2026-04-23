using System.Collections.Generic;
using UnityEngine;

namespace Parsek.InGameTests
{
    /// <summary>
    /// Phase 9 of Rewind-to-Staging (design §6.6 step 4 / §7.13 / §7.14 /
    /// §11.5): end-to-end runtime test for contract state stickiness across
    /// a re-fly merge. Runs in FLIGHT so the live <see cref="ParsekScenario"/>
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
    ///   <item><description>NO <see cref="LedgerTombstone"/> was written against any
    ///       contract action (v1 narrow scope — §7.13 / §7.14).</description></item>
    ///   <item><description>The contract action is still visible in the Effective Ledger
    ///       Set (<see cref="EffectiveState.ComputeELS"/>).</description></item>
    /// </list>
    /// </summary>
    public class ContractStickyAcrossSupersedeTest
    {
        [InGameTest(Category = "Rewind", Scene = GameScenes.FLIGHT,
            Description = "Re-fly merge does NOT tombstone contract actions from the superseded subtree (§7.13/§7.14)")]
        public void ContractStickyAcrossSupersede()
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
            // for stickiness.
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
                $"ContractStickyAcrossSupersede: found {contractActionIds.Count} contract action(s) " +
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

            // Invariant 2: NO tombstone was written against any contract action.
            var tombs = scenario.LedgerTombstones ?? new List<LedgerTombstone>();
            foreach (var t in tombs)
            {
                if (t == null || string.IsNullOrEmpty(t.ActionId)) continue;
                InGameAssert.IsFalse(contractActionIds.Contains(t.ActionId),
                    $"Contract action '{t.ActionId}' must NOT be tombstoned (§7.13/§7.14)");
            }

            // Invariant 3: every contract action still appears in ELS (design §3.2 —
            // contracts remain in ELS even when their source recording is superseded).
            var els = EffectiveState.ComputeELS();
            var inEls = new HashSet<string>();
            foreach (var a in els)
            {
                if (a == null || string.IsNullOrEmpty(a.ActionId)) continue;
                if (contractActionIds.Contains(a.ActionId))
                    inEls.Add(a.ActionId);
            }
            InGameAssert.AreEqual(contractActionIds.Count, inEls.Count,
                $"All contract actions must remain in ELS; expected {contractActionIds.Count}, got {inEls.Count}");

            ParsekLog.Info("RewindTest",
                $"ContractStickyAcrossSupersede: all {contractActionIds.Count} contract action(s) " +
                $"survive in Ledger.Actions + ELS; zero contract tombstones written.");
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
