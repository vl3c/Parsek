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

            // SUBTREE MEMBERSHIP IS NOT ENOUGH, and RF-12W's first flight is what
            // proved it. CommitTombstones applies a SECOND screen the gather below
            // must mirror: PreRewindTombstoneGuard KEEPS an in-subtree action whose
            // UT lies strictly before the rewind cutoff, because that part of the
            // timeline is the part the merge KEEPS (SupersedeCommit
            // `TOMBSTONE-SCOPE-HAS-NO-UT-GUARD`, mirroring RecordingTreeSplitter's
            // step-2.9 ledger retag bit for bit). A crewed flight rewound after
            // launch ALWAYS produces such a row: the crew boards at launch, so its
            // KerbalAssignment action's UT precedes any rewind point taken later.
            //
            // Without this partition the expected set is a strict SUPERSET of what
            // the merge can ever write, and the cell reds against documented,
            // headlessly-pinned product behaviour. RF-12W measured exactly that on
            // 2026-09-09: `PreRewindTombstoneGuard: keep action=act_482bbdec...
            // ut=29.939999999999451 cutoffUT=131.53999999999348`, then this cell
            // failing on "must be tombstoned after merge". That is the SECOND time
            // this cell asserted against an unmet precondition (the first was the
            // 2026-08-04 unflown-provisional case guarded below), which is why the
            // screen calls the PRODUCT predicate rather than re-deriving the
            // comparison here - a private copy of `a.UT < cutoff` is exactly the
            // kind of second convention the helper's own contract forbids.
            double rewindCutoffUT = SupersedeCommit.ComputeTombstoneRewindCutoffUT(marker);
            var rows = new List<StraddlingDeathRow>();
            foreach (var a in Ledger.Actions)
            {
                if (a == null) continue;
                if (string.IsNullOrEmpty(a.RecordingId)) continue;
                if (!subtreeSet.Contains(a.RecordingId)) continue;
                if (a.Type != GameActionType.KerbalAssignment) continue;
                if (a.KerbalEndStateField != KerbalEndState.Dead) continue;
                rows.Add(new StraddlingDeathRow(
                    a.ActionId, a.KerbalName,
                    TombstoneAttributionHelper.IsPreRewindAttributedAction(a, rewindCutoffUT)));
            }

            HashSet<string> deathActionIds;
            HashSet<string> deadKerbalNames;
            string straddleSkip;
            if (!TryPartitionSubtreeDeaths(rows, rewindCutoffUT,
                    out deathActionIds, out deadKerbalNames, out straddleSkip))
            {
                InGameAssert.Skip(straddleSkip);
                return;
            }

            // The merge can only tombstone THROUGH a supersede, and SupersedeCommit
            // deliberately REFUSES to supersede an unflown provisional:
            // `AppendRelations outcome=refused-unflown-provisional ... reason=empty
            // Points -- the re-fly attempt has no playable trajectory, so it cannot
            // replace the origin; writing 0 supersede rows and completing the merge
            // (origin stays effective)`. With zero supersede rows there is no
            // in-scope action for CommitTombstones to retire, so the §7.16 assertion
            // below has an UNMET PRECONDITION and would red against CORRECT product
            // behaviour.
            //
            // FOUND 2026-08-04 by the R7b spec, which arms a real session with
            // InvokeRewind but never flies the re-fly: the merge logged that refusal
            // and `Tombstoned 0 career actions (... Kerbal=0 ...)`, and this test red
            // on "must be tombstoned after merge". Skipping NAMES the missing context
            // per the house rule instead of asserting against an assumed one. On a
            // flown re-fly - CL-3's shape, which measures `Tombstoned 1 career
            // actions (... Kerbal=1 ...)` - this guard is false and every assertion
            // below runs unchanged, so nothing is weakened.
            //
            // The guard calls the PRODUCT predicate itself rather than approximating
            // it, so it can never drift from what the merge actually refuses on:
            // ValidateSupersedeTarget accepts OrbitSegments / TrackSections payload
            // as well as Points, and ALSO refuses on a null TerminalState (a re-fly
            // still in flight), which a bare Points check would wave through into a
            // red.
            string supersedeRefusal;
            if (!SupersedeCommit.ValidateSupersedeTarget(provisional, out supersedeRefusal))
            {
                InGameAssert.Skip(
                    "Provisional '" + (provisional.RecordingId ?? "<no-id>") +
                    "' is not a valid supersede target (" + (supersedeRefusal ?? "<no-reason>") +
                    "), so SupersedeCommit refuses to supersede the origin and no " +
                    "tombstone can be written. Fly the re-fly to completion before " +
                    "running this test.");
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

        /// <summary>
        /// One in-subtree KerbalAssignment+Dead row, reduced to the three facts the
        /// partition needs. <c>KeptByPreRewindGuard</c> is the PRODUCT predicate's
        /// answer (<see cref="TombstoneAttributionHelper.IsPreRewindAttributedAction"/>),
        /// passed in rather than re-derived, so this decision can never drift from
        /// what <c>CommitTombstones</c> actually screens on.
        /// </summary>
        internal readonly struct StraddlingDeathRow
        {
            internal readonly string ActionId;
            internal readonly string KerbalName;
            internal readonly bool KeptByPreRewindGuard;

            internal StraddlingDeathRow(string actionId, string kerbalName, bool keptByPreRewindGuard)
            {
                ActionId = actionId;
                KerbalName = kerbalName;
                KeptByPreRewindGuard = keptByPreRewindGuard;
            }
        }

        /// <summary>
        /// Split the subtree's kerbal-death rows into what the merge WILL retire and
        /// which kerbals section 7.16 can therefore recover, or refuse the run by naming the
        /// missing context. Pure, so it is pinned headlessly in
        /// <c>KerbalRecoveryStraddleGuardTests</c> the way PR #1661 pinned
        /// <c>MergeInterruptionRecoveryTest.TryBuildUnconcludedReFlySkip</c>.
        ///
        /// <para><b>The two outputs answer different questions and must be built
        /// differently.</b> <paramref name="tombstonableActionIds"/> is per-ACTION:
        /// a post-cutoff row is in scope and the merge retires it, so asserting on it
        /// is fair. <paramref name="recoverableKerbalNames"/> is per-KERBAL, and a
        /// kerbal who owns even ONE kept row cannot be recovered no matter how many
        /// of their other rows are retired - the kept row goes on holding a permanent
        /// Dead reservation, which is the state RF-12W's log shows verbatim
        /// (<c>Recomputed after tombstones: 2 reservations remain (permanent=2
        /// temporary=0)</c>). Asserting the roster half against such a kerbal is
        /// asserting against correct behaviour.</para>
        ///
        /// <para>Refuses (returns false) in two cases: no death rows at all, and no
        /// RECOVERABLE kerbal. The second is the RF-12W shape and it is a genuine
        /// missing precondition rather than a weakened assertion: with every
        /// candidate kerbal straddling the rewind, invariant 2 has no subject, so the
        /// cell would be half a test wearing a whole test's name. The skip text names
        /// the kept ids, the kerbals and the cutoff so a reader can tell this apart
        /// from an empty subtree at a glance.</para>
        /// </summary>
        internal static bool TryPartitionSubtreeDeaths(
            IList<StraddlingDeathRow> rows,
            double rewindCutoffUT,
            out HashSet<string> tombstonableActionIds,
            out HashSet<string> recoverableKerbalNames,
            out string skipMessage)
        {
            tombstonableActionIds = new HashSet<string>();
            recoverableKerbalNames = new HashSet<string>();
            skipMessage = null;

            var keptActionIds = new List<string>();
            var straddlingKerbals = new HashSet<string>();
            var candidateKerbals = new HashSet<string>();

            int rowCount = rows == null ? 0 : rows.Count;
            for (int i = 0; i < rowCount; i++)
            {
                StraddlingDeathRow row = rows[i];
                if (!string.IsNullOrEmpty(row.KerbalName))
                    candidateKerbals.Add(row.KerbalName);
                if (row.KeptByPreRewindGuard)
                {
                    if (!string.IsNullOrEmpty(row.ActionId))
                        keptActionIds.Add(row.ActionId);
                    if (!string.IsNullOrEmpty(row.KerbalName))
                        straddlingKerbals.Add(row.KerbalName);
                    continue;
                }
                if (!string.IsNullOrEmpty(row.ActionId))
                    tombstonableActionIds.Add(row.ActionId);
            }

            foreach (string name in candidateKerbals)
            {
                if (!straddlingKerbals.Contains(name))
                    recoverableKerbalNames.Add(name);
            }

            if (rowCount == 0)
            {
                skipMessage =
                    "No kerbal-death actions in supersede subtree - create a BG-crash "
                    + "with kerbals aboard before running this test.";
                return false;
            }

            if (recoverableKerbalNames.Count == 0)
            {
                skipMessage =
                    "Every kerbal-death row in the supersede subtree STRADDLES the rewind: "
                    + "its action UT precedes cutoffUT="
                    + rewindCutoffUT.ToString("R", System.Globalization.CultureInfo.InvariantCulture)
                    + ", so PreRewindTombstoneGuard keeps it on the timeline the merge "
                    + "KEEPS and the kerbal stays permanently reserved Dead. Kept action(s): "
                    + string.Join(", ", keptActionIds.ToArray())
                    + "; kerbal(s): " + string.Join(", ", new List<string>(straddlingKerbals).ToArray())
                    + ". The recovery invariant has no subject here. Run this test on a "
                    + "re-fly whose crew BOARDED after the rewind point (a BG-crash with "
                    + "kerbals aboard, not a crewed stack rewound after launch).";
                return false;
            }

            return true;
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
