using System;
using System.Collections.Generic;

namespace Parsek
{
    public static partial class EffectiveState
    {
        /// <summary>
        /// The supersede rows of the loaded scenario, or an empty list when none is
        /// loaded (headless tests, the main menu). For callers outside this module that
        /// need the rows for <see cref="ResolveMemberSealState"/>.
        /// </summary>
        internal static IReadOnlyList<RecordingSupersedeRelation> CurrentSupersedesOrEmpty()
        {
            return ParsekScenario.Instance?.RecordingSupersedes
                ?? (IReadOnlyList<RecordingSupersedeRelation>)Array.Empty<RecordingSupersedeRelation>();
        }

        /// <summary>
        /// The MergeState a tree member counts as for a "can this tree still change"
        /// gate (route candidacy, the SealSlot seam). A Rewind Point slot's open /
        /// closed bit lives on its effective chain+supersede tip only: Seal, Stash, the
        /// load-time sweeps and the Re-Fly merge flip that one recording, and the
        /// commit promotion and the optimizer split leave a chain HEAD (or a
        /// superseded recording) at CommittedProvisional that no later write closes.
        /// So the member is read through the same walk every slot consumer uses
        /// (<see cref="EffectiveTipRecordingId(string, IReadOnlyList{RecordingSupersedeRelation}, IReadOnlyDictionary{string, Recording}, RecordingTree)"/>),
        /// and the recording it resolves to decides.
        ///
        /// <para>Rules, in order: a null member reads Immutable (callers skip it); a
        /// NotCommitted member reads NotCommitted whatever its tip says (a live
        /// recorder's row is never finished); a member that resolves to itself, or to
        /// a recording that cannot be found, reads its own state; otherwise the
        /// resolved recording's state. The walk is deterministic, so a slot's tip
        /// resolves to itself and an open slot always reads open. The read is
        /// symmetric: an Immutable member whose walk ends on an open tip reads open.
        /// Pure apart from the committed-store fallback lookup of a tip outside
        /// <paramref name="tree"/>.</para>
        /// </summary>
        internal static MergeState ResolveMemberSealState(
            Recording rec,
            string memberKey,
            RecordingTree tree,
            IReadOnlyList<RecordingSupersedeRelation> supersedes)
        {
            if (rec == null)
                return MergeState.Immutable;
            if (rec.MergeState == MergeState.NotCommitted)
                return MergeState.NotCommitted;

            string id = !string.IsNullOrEmpty(rec.RecordingId) ? rec.RecordingId : memberKey;
            if (string.IsNullOrEmpty(id))
                return rec.MergeState;

            string tipId = EffectiveTipRecordingId(id, supersedes, tree?.Recordings, tree);
            if (string.IsNullOrEmpty(tipId) || string.Equals(tipId, id, StringComparison.Ordinal))
                return rec.MergeState;

            Recording tip = null;
            if (tree?.Recordings != null)
                tree.Recordings.TryGetValue(tipId, out tip);
            if (tip == null)
                tip = FindCommittedRecordingByIdRaw(tipId);
            return tip != null ? tip.MergeState : rec.MergeState;
        }
    }
}
