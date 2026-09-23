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
        /// an origin-tagged action to TIP iff its attribution UT
        /// (<see cref="ComputeAttributionUT"/>: <c>UT</c>, or <c>EndUT</c> for a
        /// death-encoding KerbalAssignment) is <c>&gt;= rewindUT</c>, and leaves
        /// everything earlier on the kept HEAD. Both sides read the key through
        /// that one helper, never a raw <c>UT</c>. Keeping the two predicates
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
            double attributionUT = ComputeAttributionUT(action);
            if (double.IsNaN(attributionUT)) return false;
            return attributionUT < rewindCutoffUT;
        }

        /// <summary>
        /// TOMBSTONE-GUARD-SCREENS-AN-INTERVAL-ACTION-BY-ITS-START: the timeline UT
        /// that decides which side of a rewind cut an action belongs to. It is the
        /// SINGLE screening key for both sides of the seam:
        /// <see cref="IsPreRewindAttributedAction"/> (the tombstone write-set) and
        /// <c>RecordingTreeSplitter.ShouldRetagLedgerActionToTip</c> (the step-2.9
        /// ledger retag). Neither side may compare a raw <see cref="GameAction.UT"/>
        /// on its own, or the pair stops being bit-identical.
        ///
        /// <para>
        /// A <see cref="GameActionType.KerbalAssignment"/> whose encoded outcome is
        /// <see cref="KerbalEndState.Dead"/> is an interval (boarding at
        /// <see cref="GameAction.StartUT"/>, death at <see cref="GameAction.EndUT"/>),
        /// and the event a merge refunds is the DEATH, not the boarding. It is
        /// screened by its <see cref="GameAction.EndUT"/>, so a crew member who
        /// boarded before the rewind point and died after it on the superseded
        /// branch is recovered by the merge (design 7.16). A re-fly that kills the
        /// crew again files its OWN death row under the provisional's id at tree
        /// commit (<c>LedgerOrchestrator.NotifyLedgerTreeCommitted</c> runs before
        /// <c>MergeDialog.TryCommitReFlySupersede</c>), and the supersede closure
        /// never contains the provisional, so this clause cannot reach that row.
        /// </para>
        ///
        /// <para>
        /// Every other action, interval-shaped or not (a non-death KerbalAssignment
        /// included), stays on <see cref="GameAction.UT"/>. A death row with a NaN
        /// EndUT has no known death instant and falls back to
        /// <see cref="GameAction.UT"/>. NaN comes back only when the chosen key is
        /// NaN; callers treat it as "not provably pre-rewind".
        /// </para>
        ///
        /// <para>
        /// TWO KNOWN LIMITS (TOMBSTONE-ENDUT-SCREEN-LOW-LIMITS). (1)
        /// <see cref="GameAction.EndUT"/> is a float: late in a career (UT around 2e7 s,
        /// where a float step is 2 s) a death within about 1 s after the rewind point can
        /// round BELOW the double cutoff and stay kept, while the paired KerbalDeath
        /// reputation row (double UT) is refunded. Screening on the owning recording's
        /// double EndUT is not a drop-in: the splitter's step 2.9 runs after
        /// <c>SplitAtUT</c> has already truncated the origin to the rewind point, so the
        /// two sides would read different recordings and the pair would stop being
        /// bit-identical. (2) A Destroyed terminal marks every START crew member Dead at
        /// the recording's end, so a kerbal who actually died BEFORE the rewind on a vessel
        /// that flew on past it carries an end-of-recording EndUT and is now tombstoned.
        /// Both are rare; neither changes the mirror. Both are ACCEPTED (ruling of
        /// 2026-09-23): (1) is logged by <c>SupersedeCommit.CommitTombstones</c> through
        /// <see cref="IsDeathEndUTWithinFloatStepOfCutoff"/>, (2) is a documented known
        /// limitation with no code.
        /// </para>
        /// </summary>
        internal static double ComputeAttributionUT(GameAction action)
        {
            if (action == null) return double.NaN;
            if (IsDeathEncodingInterval(action))
                return (double)action.EndUT;
            return action.UT;
        }

        /// <summary>
        /// True iff <paramref name="action"/> is a KerbalAssignment whose end state is
        /// Dead and whose EndUT is known: the one action class screened by its end
        /// (see <see cref="ComputeAttributionUT"/>).
        /// </summary>
        internal static bool IsDeathEncodingInterval(GameAction action)
        {
            return action != null
                && action.Type == GameActionType.KerbalAssignment
                && action.KerbalEndStateField == KerbalEndState.Dead
                && !float.IsNaN(action.EndUT);
        }

        /// <summary>
        /// TOMBSTONE-ENDUT-SCREEN-LOW-LIMITS limit (1), accepted and logged per the
        /// 2026-09-23 ruling: true iff <paramref name="action"/> is a death-encoding
        /// interval whose float <see cref="GameAction.EndUT"/> lies within one float step
        /// (the spacing of single-precision values at the cutoff's magnitude) of
        /// <paramref name="rewindCutoffUT"/>. Inside that band the float cannot say which
        /// side of the rewind the death was on, so the screen's answer may be the
        /// rounding's rather than the flight's. Callers log it; the screen is unchanged.
        /// </summary>
        internal static bool IsDeathEndUTWithinFloatStepOfCutoff(
            GameAction action, double rewindCutoffUT, out double floatStep)
        {
            floatStep = FloatStepAt(rewindCutoffUT);
            if (!IsDeathEncodingInterval(action)) return false;
            if (double.IsNaN(rewindCutoffUT) || double.IsNaN(floatStep)) return false;
            return System.Math.Abs((double)action.EndUT - rewindCutoffUT) <= floatStep;
        }

        /// <summary>
        /// The gap between the single-precision value nearest <paramref name="ut"/> and the
        /// next representable one above it (2 s at UT 2e7, about 4e-6 s at UT 34). NaN for a
        /// non-finite input.
        /// </summary>
        internal static double FloatStepAt(double ut)
        {
            if (double.IsNaN(ut) || double.IsInfinity(ut)) return double.NaN;
            float f = (float)System.Math.Abs(ut);
            if (float.IsInfinity(f)) return double.NaN;
            int bits = System.BitConverter.ToInt32(System.BitConverter.GetBytes(f), 0);
            float next = System.BitConverter.ToSingle(System.BitConverter.GetBytes(bits + 1), 0);
            return (double)next - (double)f;
        }
    }
}
