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
        /// </summary>
        public static bool InSupersedeScope(GameAction action, ICollection<string> subtreeIds)
        {
            if (action == null) return false;
            if (string.IsNullOrEmpty(action.RecordingId)) return false; // §7.41
            if (subtreeIds == null || subtreeIds.Count == 0) return false;
            return subtreeIds.Contains(action.RecordingId);
        }
    }
}
