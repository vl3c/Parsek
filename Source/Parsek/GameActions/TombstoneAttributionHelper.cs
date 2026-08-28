using System.Collections.Generic;

namespace Parsek
{
    /// <summary>
    /// Phase 9 of Rewind-to-Staging (design §3.3 closure / §6.6 step 4 / §7.41):
    /// subtree-attribution helper for ledger actions relative to the forward-only
    /// merge-guarded subtree closure rooted at the supersede target.
    ///
    /// <para>
    /// An action is "in scope" for Phase 9 tombstoning iff its
    /// <see cref="GameAction.RecordingId"/> is non-null AND the id is a member
    /// of the subtree closure. Null-scoped actions are never in scope (§7.41 —
    /// they represent career / KSC / system events not attributable to any
    /// single recording in the superseded branch).
    /// </para>
    ///
    /// <para>
    /// The closure itself is computed by
    /// <see cref="EffectiveState.ComputeSessionSuppressedSubtree"/> using the
    /// same walk that session-suppresses physical-visibility during a live
    /// re-fly; Phase 9 simply re-uses the already-cached subtree ids at merge
    /// time.
    /// </para>
    /// </summary>
    internal static class TombstoneAttributionHelper
    {
        /// <summary>
        /// True iff <paramref name="action"/> carries a non-null
        /// <see cref="GameAction.RecordingId"/> that <paramref name="subtreeIds"/>
        /// contains. Null / empty inputs return false (safe pass-through).
        ///
        /// <para>
        /// Subtree containment alone is NOT the tombstone write-set: a row whose UT
        /// lies strictly before the rewind point was earned on the part of the
        /// timeline the merge KEEPS, even when its <see cref="GameAction.RecordingId"/>
        /// names a subtree member. Callers must additionally screen with
        /// <see cref="IsPreRewindAttributedAction"/> — see
        /// <c>SupersedeCommit.CommitTombstones</c>.
        /// </para>
        /// </summary>
        public static bool InSupersedeScope(GameAction action, ICollection<string> subtreeIds)
        {
            if (action == null) return false;
            if (string.IsNullOrEmpty(action.RecordingId)) return false; // §7.41
            if (subtreeIds == null || subtreeIds.Count == 0) return false;
            return subtreeIds.Contains(action.RecordingId);
        }

        /// <summary>
        /// TOMBSTONE-SCOPE-HAS-NO-UT-GUARD: true iff <paramref name="action"/> is
        /// provably attributed to the PRE-rewind half of the timeline and must
        /// therefore survive a merge even though
        /// <see cref="InSupersedeScope"/> accepts it.
        ///
        /// <para>
        /// The predicate is the exact mirror of <c>RecordingTreeSplitter</c>'s
        /// step-2.9 ledger retag on the other side of the same seam, which moves
        /// an origin-tagged action to TIP iff <c>a.UT &gt;= rewindUT</c> and leaves
        /// everything earlier on the kept HEAD. Keeping the two predicates
        /// bit-identical (raw <c>rewindUT</c>, no epsilon, same comparison sense)
        /// is what makes "the split kept it, so the tombstone pass keeps it too"
        /// true rather than approximately true. Do NOT introduce an epsilon here
        /// to match <c>SupersedeCommit.ComputePreRewindCutoff</c>'s debris cutoff:
        /// that helper deliberately biases a DIFFERENT boundary
        /// (see its remarks) and a third convention on this seam would let a row
        /// the splitter left on HEAD still be tombstoned.
        /// </para>
        ///
        /// <para>
        /// <paramref name="rewindCutoffUT"/> is <c>double.NaN</c> for a legacy
        /// marker that carries neither a rewind-point UT nor an invoked UT; the
        /// guard is then inert (no cutoff available =&gt; legacy behavior). A
        /// NaN action UT is likewise not provably pre-rewind and stays in scope.
        /// </para>
        /// </summary>
        public static bool IsPreRewindAttributedAction(GameAction action, double rewindCutoffUT)
        {
            if (action == null) return false;
            if (double.IsNaN(rewindCutoffUT)) return false;
            if (double.IsNaN(action.UT)) return false;
            return action.UT < rewindCutoffUT;
        }
    }
}
