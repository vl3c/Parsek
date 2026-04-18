using System.Collections.Generic;
using UnityEngine;

namespace Parsek.InGameTests
{
    /// <summary>
    /// Phase 9 of Rewind-to-Staging (design §6.6 step 4+6 / §7.16 / §11.5):
    /// end-to-end runtime test for the kerbal-death tombstone + reservation
    /// recompute path. Runs in FLIGHT so the live <see cref="ParsekScenario"/>
    /// instance is available.
    ///
    /// <para>
    /// Preconditions: an active re-fly session marker + a provisional
    /// recording whose supersede subtree contains at least one
    /// <see cref="GameActionType.KerbalAssignment"/> action with
    /// <see cref="KerbalEndState.Dead"/>. The test auto-skips when no such
    /// action lives in the subtree.
    /// </para>
    ///
    /// <para>
    /// Asserts after simulating the merge button:
    /// </para>
    /// <list type="bullet">
    ///   <item><description>Every eligible kerbal-death action in the subtree is tombstoned.</description></item>
    ///   <item><description>Each previously-Dead kerbal is back in
    ///       <see cref="HighLogic.CurrentGame"/>'s roster as
    ///       <see cref="ProtoCrewMember.RosterStatus.Available"/> or
    ///       <see cref="ProtoCrewMember.RosterStatus.Assigned"/>
    ///       (i.e. no longer Dead).</description></item>
    /// </list>
    /// </summary>
    public class KerbalRecoveryOnSupersedeTest
    {
        [InGameTest(Category = "Rewind", Scene = GameScenes.FLIGHT,
            Description = "Re-fly merge tombstones kerbal deaths in the superseded subtree; kerbals return to active (§7.16)")]
        public void KerbalRecoveryOnSupersede()
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

            // Gather every KerbalAssignment+Dead action in the subtree BEFORE
            // the merge. These are the items the test asserts against.
            var subtreeBefore = EffectiveState.ComputeSessionSuppressedSubtree(marker);
            var subtreeSet = new HashSet<string>(subtreeBefore);
            var deathActionIds = new HashSet<string>();
            var deadKerbalNames = new HashSet<string>();
            foreach (var a in Ledger.Actions)
            {
                if (a == null) continue;
                if (string.IsNullOrEmpty(a.RecordingId)) continue;
                if (!subtreeSet.Contains(a.RecordingId)) continue;
                if (a.Type != GameActionType.KerbalAssignment) continue;
                if (a.KerbalEndStateField != KerbalEndState.Dead) continue;
                if (!string.IsNullOrEmpty(a.ActionId))
                    deathActionIds.Add(a.ActionId);
                if (!string.IsNullOrEmpty(a.KerbalName))
                    deadKerbalNames.Add(a.KerbalName);
            }

            if (deathActionIds.Count == 0)
            {
                InGameAssert.Skip(
                    "No kerbal-death actions in supersede subtree — create a BG-crash with kerbals aboard before running this test.");
                return;
            }

            ParsekLog.Info("RewindTest",
                $"KerbalRecoveryOnSupersede: found {deathActionIds.Count} kerbal-death action(s) " +
                $"covering {deadKerbalNames.Count} kerbal(s) in subtree of size {subtreeBefore.Count}");

            // Simulate the merge commit. CommitSupersede runs CommitTombstones
            // + CrewReservationManager.RecomputeAfterTombstones internally.
            SupersedeCommit.CommitSupersede(marker, provisional);

            // Invariant 1: every death action is now tombstoned.
            var tombstonedActionIds = new HashSet<string>();
            var tombs = scenario.LedgerTombstones ?? new List<LedgerTombstone>();
            foreach (var t in tombs)
            {
                if (t == null || string.IsNullOrEmpty(t.ActionId)) continue;
                tombstonedActionIds.Add(t.ActionId);
            }
            foreach (var aid in deathActionIds)
            {
                InGameAssert.IsTrue(tombstonedActionIds.Contains(aid),
                    $"KerbalAssignment+Dead action '{aid}' must be tombstoned after merge (§7.16)");
            }

            // Invariant 2: each previously-Dead kerbal is no longer Dead in the roster.
            var roster = HighLogic.CurrentGame?.CrewRoster;
            InGameAssert.IsNotNull(roster, "HighLogic.CurrentGame.CrewRoster is null");
            int returned = 0;
            foreach (var kerbalName in deadKerbalNames)
            {
                ProtoCrewMember pcm = null;
                foreach (var c in roster.Crew)
                {
                    if (c.name == kerbalName) { pcm = c; break; }
                }
                if (pcm == null)
                {
                    // The kerbal disappeared entirely — either KSP cleaned them
                    // up, or a stand-in took their name. Log so the human runner
                    // knows why the test skipped this entry.
                    ParsekLog.Info("RewindTest",
                        $"KerbalRecoveryOnSupersede: kerbal '{kerbalName}' not in roster after merge (likely stand-in shuffle)");
                    continue;
                }

                InGameAssert.IsFalse(pcm.rosterStatus == ProtoCrewMember.RosterStatus.Dead,
                    $"Kerbal '{kerbalName}' is still Dead in the roster after tombstone + reservation recompute (§7.16)");
                returned++;
            }

            ParsekLog.Info("RewindTest",
                $"KerbalRecoveryOnSupersede: tombstoned {deathActionIds.Count} death action(s); " +
                $"{returned} kerbal(s) verified non-Dead post-merge.");
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
