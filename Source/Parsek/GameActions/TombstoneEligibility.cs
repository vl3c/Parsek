using System;
using System.Collections.Generic;

namespace Parsek
{
    /// <summary>
    /// Phase 9 of Rewind-to-Staging (design §6.6 step 4 / §7.13 / §7.14 / §7.15 /
    /// §7.16 / §7.41 / §7.44): v1 tombstone-eligibility classifier for ledger
    /// actions in the supersede subtree of a merged re-fly session.
    ///
    /// <para>
    /// v1 narrow scope retires ONLY <see cref="GameActionType.KerbalAssignment"/>
    /// actions whose <see cref="GameAction.KerbalEndStateField"/> is
    /// <see cref="KerbalEndState.Dead"/>, plus <see cref="GameActionType.ReputationPenalty"/>
    /// actions paired with one of those kerbal-death actions on the same
    /// <see cref="GameAction.RecordingId"/> within a tight UT window. Contract
    /// completions / failures / cancels / accepts, milestones, facility upgrades
    /// / repairs / destruction, strategies, tech research, science spending,
    /// funds spending, vessel-destruction rep penalties, and all other action
    /// types stay in ELS even when their source recording is superseded
    /// (§7.13-§7.15, §7.44).
    /// </para>
    ///
    /// <para>
    /// Null-scoped actions (<see cref="GameAction.RecordingId"/> == null) are
    /// never tombstoned, because "no recording is the source" means the action
    /// isn't attributable to the superseded subtree (§7.41).
    /// </para>
    /// </summary>
    internal static class TombstoneEligibility
    {
        /// <summary>
        /// Maximum allowed |UT delta| between a <see cref="GameActionType.ReputationPenalty"/>
        /// candidate and the <see cref="GameActionType.KerbalAssignment"/>+Dead
        /// action it pairs with. The two are emitted by the same KSP death-event
        /// callback path, so in practice the UT delta is zero; 1.0s is a safety
        /// margin for sampling jitter and floating-point drift.
        /// </summary>
        internal const double BundledRepUtWindow = 1.0;

        /// <summary>
        /// True iff <paramref name="action"/> is v1 tombstone-eligible on its
        /// own — i.e. a <see cref="GameActionType.KerbalAssignment"/> whose
        /// <see cref="GameAction.KerbalEndStateField"/> is
        /// <see cref="KerbalEndState.Dead"/> and whose
        /// <see cref="GameAction.RecordingId"/> is non-null (§7.16, §7.41).
        ///
        /// <para>
        /// Bundled <see cref="GameActionType.ReputationPenalty"/> actions are
        /// NOT eligible via this method; they require the pairing context
        /// supplied to <see cref="TryPairBundledRepPenalty"/>. Callers asking
        /// "is this action eligible?" for a rep penalty must route through
        /// <see cref="TryPairBundledRepPenalty"/> with the same-recording slice
        /// of the ledger.
        /// </para>
        /// </summary>
        public static bool IsEligible(GameAction action)
        {
            if (action == null) return false;
            if (string.IsNullOrEmpty(action.RecordingId)) return false; // §7.41
            if (action.Type != GameActionType.KerbalAssignment) return false;
            return action.KerbalEndStateField == KerbalEndState.Dead;
        }

        /// <summary>
        /// Pairing rule for <see cref="GameActionType.ReputationPenalty"/>
        /// actions. A rep penalty is v1 tombstone-eligible iff it shares a
        /// <see cref="GameAction.RecordingId"/> with a tombstone-eligible
        /// kerbal-death action (per <see cref="IsEligible"/>) and their UT
        /// values are within <see cref="BundledRepUtWindow"/> seconds of each
        /// other.
        ///
        /// <para>
        /// Null-scoped candidates (<see cref="GameAction.RecordingId"/> == null)
        /// are not eligible (§7.41); a non-matching
        /// <see cref="GameAction.Type"/> is not eligible either. If no paired
        /// death action is found (e.g. vessel-destruction rep, which carries no
        /// kerbal death on its recording — §7.44), the method returns false
        /// and <paramref name="pairedDeathAction"/> is null.
        /// </para>
        ///
        /// <para>
        /// <paramref name="sameRecordingActions"/> is expected to be the slice
        /// of the ledger whose <see cref="GameAction.RecordingId"/> matches
        /// <paramref name="candidate"/>'s — the caller builds this once per
        /// supersede-scope pass (see <see cref="SupersedeCommit"/>). Null or
        /// empty list returns false.
        /// </para>
        /// </summary>
        public static bool TryPairBundledRepPenalty(
            GameAction candidate,
            IReadOnlyList<GameAction> sameRecordingActions,
            out GameAction pairedDeathAction)
        {
            pairedDeathAction = null;
            if (candidate == null) return false;
            if (candidate.Type != GameActionType.ReputationPenalty) return false;
            if (string.IsNullOrEmpty(candidate.RecordingId)) return false; // §7.41
            if (sameRecordingActions == null || sameRecordingActions.Count == 0) return false;

            for (int i = 0; i < sameRecordingActions.Count; i++)
            {
                var other = sameRecordingActions[i];
                if (other == null) continue;
                if (!IsEligible(other)) continue;
                if (!string.Equals(other.RecordingId, candidate.RecordingId, StringComparison.Ordinal))
                    continue;

                double delta = Math.Abs(other.UT - candidate.UT);
                if (delta <= BundledRepUtWindow)
                {
                    pairedDeathAction = other;
                    return true;
                }
            }
            return false;
        }
    }
}
