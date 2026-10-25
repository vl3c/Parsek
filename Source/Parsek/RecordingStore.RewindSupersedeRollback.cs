using System;
using System.Collections.Generic;

namespace Parsek
{
    public static partial class RecordingStore
    {
        /// <summary>
        /// At rewind time, classifies the <see cref="RecordingSupersedeRelation"/> rows
        /// of the rewound owner's tree whose fork (<c>NewRecordingId</c>) starts at or
        /// after <paramref name="rewindAdjustedUT"/>. A rewind moves the clock and
        /// nothing else (owner ruling 2026-10-24), so a row whose fork is a committed
        /// recording is KEPT whatever its MergeState: a sealed re-fly stays sealed and
        /// an open one stays open and re-flyable. Only a dangling row (fork missing from
        /// the committed set, or still NotCommitted) drops, since it would otherwise keep
        /// hiding its OldRecordingId with nothing standing in for it. Walks the entire
        /// tree of the rewound owner so branch recordings are classified too.
        ///
        /// Pure-static: takes the supersede list as a parameter so it's directly unit
        /// testable without Unity. Returns the number of relations dropped.
        /// </summary>
        internal static int DropSupersedesRewoundOutOfExistencePure(
            Recording owner,
            double rewindAdjustedUT,
            IReadOnlyList<Recording> ownerTreeRecordings,
            IReadOnlyDictionary<string, Recording> liveRecordingsById,
            List<RecordingSupersedeRelation> supersedes)
        {
            return DropSupersedesRewoundOutOfExistenceDetailedPure(
                owner,
                rewindAdjustedUT,
                ownerTreeRecordings,
                liveRecordingsById,
                supersedes).DroppedRelationCount;
        }

        internal static RewindSupersedeRollbackResult DropSupersedesRewoundOutOfExistenceDetailedPure(
            Recording owner,
            double rewindAdjustedUT,
            IReadOnlyList<Recording> ownerTreeRecordings,
            IReadOnlyDictionary<string, Recording> liveRecordingsById,
            List<RecordingSupersedeRelation> supersedes)
        {
            var result = new RewindSupersedeRollbackResult();
            if (owner == null || supersedes == null || supersedes.Count == 0)
                return result;

            // Build set of recording ids that are rewound out: the owner itself plus
            // every recording in the owner's tree whose StartUT >= rewindAdjustedUT.
            var rewoundOutOldIds = new HashSet<string>(StringComparer.Ordinal);
            if (!string.IsNullOrEmpty(owner.RecordingId))
                rewoundOutOldIds.Add(owner.RecordingId);
            if (ownerTreeRecordings != null)
            {
                for (int i = 0; i < ownerTreeRecordings.Count; i++)
                {
                    var treeRec = ownerTreeRecordings[i];
                    if (treeRec == null || string.IsNullOrEmpty(treeRec.RecordingId))
                        continue;
                    if (treeRec.StartUT >= rewindAdjustedUT)
                        rewoundOutOldIds.Add(treeRec.RecordingId);
                }
            }

            // A rewind moves the clock back and nothing else (owner ruling
            // 2026-10-24): a merged Re-Fly fork stays the slot's tip whatever its
            // MergeState, so a sealed fork stays sealed and an open (crashed) fork
            // stays open and re-flyable. Two-pass classification of in-scope
            // supersede relations:
            //
            //   Pass 1 — for every relation whose OldRecordingId belongs to the
            //     rewound subtree, or whose NewRecordingId is the owner, and whose
            //     fork starts at or after the rewind (preferred signal:
            //     Recording.StartUT; orphan fallback: rel.UT, the merge time
            //     recorded on the relation itself):
            //       - the fork is a committed recording (Immutable or
            //         CommittedProvisional) -> tentatively preserve;
            //       - otherwise (fork missing from the committed set, or still
            //         NotCommitted) -> drop + retire: the row dangles and would
            //         silently keep suppressing OldRecordingId.
            //
            //   Pass 2 — demote any tentative preservation whose OldRecordingId
            //     is itself being retired in this same batch. The fork's priorTip
            //     is gone, so the fork has no live source to stand in for;
            //     preserving it would leave A and C both visible alongside the
            //     restored A (the double-materialization regression of
            //     docs/dev/done/plans/fix-watch-double-probe-ghost-after-rewind.md).
            //
            // Coupling hazard: ParsekScenario.MarkRewoundTreeRecordingsAsGhostOnly
            // currently scopes SpawnSuppressedByRewind to the active/source
            // recording only (per #589). If that scope ever expands to cover
            // same-tree future recordings, the preserved fork would be silently
            // re-suppressed at spawn time even though this predicate kept its
            // supersede relation.
            var pendingDrops = new List<RecordingSupersedeRelation>();
            var pendingPreservations = new List<RecordingSupersedeRelation>();
            for (int i = 0; i < supersedes.Count; i++)
            {
                var rel = supersedes[i];
                if (rel == null || string.IsNullOrEmpty(rel.OldRecordingId)) continue;
                if (string.IsNullOrEmpty(rel.NewRecordingId)) continue;

                // In scope: the outgoing case (Old in the rewound subtree) and the
                // incoming case (New is the owner itself). Both classify the same
                // way: a rewind on the fork itself does not undo it either.
                bool oldInScope = rewoundOutOldIds.Contains(rel.OldRecordingId);
                bool newIsOwner = !string.IsNullOrEmpty(owner.RecordingId)
                    && string.Equals(rel.NewRecordingId, owner.RecordingId, StringComparison.Ordinal);
                if (!oldInScope && !newIsOwner) continue;

                Recording newRec = null;
                if (liveRecordingsById != null)
                    liveRecordingsById.TryGetValue(rel.NewRecordingId, out newRec);
                double effectiveForkUT = newRec != null ? newRec.StartUT : rel.UT;
                if (effectiveForkUT < rewindAdjustedUT) continue;

                if (newRec != null && newRec.MergeState != MergeState.NotCommitted)
                    pendingPreservations.Add(rel);
                else
                    pendingDrops.Add(rel);
            }

            // Pass 2 — demote preservations whose priorTip is itself retired,
            // iterating to a fixpoint so cascades propagate.
            //
            // pendingRetiredNewIds = { rel.NewRecordingId | rel ∈ pendingDrops }
            // (the New of each drop is the fork that gets retired; the Old gets
            // restored, so checking against Olds would be the wrong direction).
            //
            // Cascade example: A -> B(missing) -> C(Immutable) -> D(Immutable),
            // rewind past A's start.
            //   Initial: pendingDrops=[A->B], pendingPreservations=[B->C, C->D].
            //   Pass 2 iter 1: B in pendingRetiredNewIds={B} → B→C demotes;
            //     pendingRetiredNewIds becomes {B,C}.
            //   Pass 2 iter 2: C now in pendingRetiredNewIds → C→D demotes too;
            //     pendingRetiredNewIds becomes {B,C,D}.
            //   Pass 2 iter 3: no more preservations → terminate.
            // Without the fixpoint loop, C→D would survive the demotion pass
            // and D would render as canon alongside the restored A — the
            // double-materialization regression PR #776/#777 fixes.
            if (pendingPreservations.Count > 0)
            {
                var pendingRetiredNewIds = new HashSet<string>(
                    StringComparer.Ordinal);
                for (int i = 0; i < pendingDrops.Count; i++)
                {
                    string newId = pendingDrops[i].NewRecordingId;
                    if (!string.IsNullOrEmpty(newId))
                        pendingRetiredNewIds.Add(newId);
                }

                bool changedThisIteration = true;
                while (changedThisIteration)
                {
                    changedThisIteration = false;
                    for (int i = pendingPreservations.Count - 1; i >= 0; i--)
                    {
                        var rel = pendingPreservations[i];
                        if (!string.IsNullOrEmpty(rel.OldRecordingId)
                            && pendingRetiredNewIds.Contains(rel.OldRecordingId))
                        {
                            pendingPreservations.RemoveAt(i);
                            pendingDrops.Add(rel);
                            if (!string.IsNullOrEmpty(rel.NewRecordingId))
                            {
                                result.DemotedPreservationIds.Add(rel.NewRecordingId);
                                // Adding the demoted New to the retired set
                                // makes the cascade transitive: a later
                                // preservation candidate whose Old is this
                                // demoted New will also demote on the next
                                // iteration.
                                pendingRetiredNewIds.Add(rel.NewRecordingId);
                            }
                            changedThisIteration = true;
                        }
                    }
                }

                // Anything still in pendingPreservations is a confirmed
                // preservation.
                for (int i = 0; i < pendingPreservations.Count; i++)
                {
                    string newId = pendingPreservations[i].NewRecordingId;
                    if (!string.IsNullOrEmpty(newId))
                        result.PreservedForkRecordingIds.Add(newId);
                }
            }

            if (pendingDrops.Count == 0)
                return result;

            for (int i = 0; i < pendingDrops.Count; i++)
            {
                var rel = pendingDrops[i];
                result.DroppedRelations.Add(rel);
                if (!string.IsNullOrEmpty(rel.NewRecordingId))
                    result.RetiredForkRecordingIds.Add(rel.NewRecordingId);
                if (!string.IsNullOrEmpty(rel.OldRecordingId))
                    result.RestoredRecordingIds.Add(rel.OldRecordingId);
                supersedes.Remove(pendingDrops[i]);
            }

            foreach (string retiredId in result.RetiredForkRecordingIds)
                result.RestoredRecordingIds.Remove(retiredId);

            // Also prune any preserved fork ids: a recording can be both the
            // Old of a dropped relation (added to RestoredRecordingIds by the
            // apply loop above) AND the New of a preserved relation (in
            // PreservedForkRecordingIds, hence the slot's head). Example:
            // A -> B(Imm) -> C(missing) rewind past A. B is preserved (A->B
            // kept) but is also the priorTip of the dangling B->C, so it lands
            // in RestoredRecordingIds. The live caller's old-side pass
            // iterates RestoredRecordingIds and would retire B, leaving NO
            // head visible (A hidden by the surviving A->B). Preservation
            // must win: anything in PreservedForkRecordingIds stays visible.
            foreach (string preservedId in result.PreservedForkRecordingIds)
                result.RestoredRecordingIds.Remove(preservedId);

            return result;
        }

        /// <summary>
        /// Returns true when at least one dropped relation targeting
        /// <paramref name="oldSideId"/> as its OldRecordingId has a fork
        /// (NewRecordingId) resolving to an <see cref="MergeState.Immutable"/>
        /// recording. That configuration (reachable only through a Pass-2
        /// demotion) represents a permanent (canon) supersede the rewind
        /// erased, so the priorTip stays retired. Any other configuration —
        /// non-Immutable fork or missing fork - returns false so the priorTip
        /// can become visible again.
        ///
        /// Internal (not private) so the truth-table xUnit covers each branch
        /// without going through the full live entry point.
        /// </summary>
        internal static bool AnyDroppedRelationRetiresPriorTipPermanently(
            string oldSideId,
            RewindSupersedeRollbackResult rollback,
            IReadOnlyDictionary<string, Recording> liveRecordingsById)
        {
            if (string.IsNullOrEmpty(oldSideId)
                || rollback?.DroppedRelations == null
                || rollback.DroppedRelations.Count == 0)
                return false;

            for (int i = 0; i < rollback.DroppedRelations.Count; i++)
            {
                var rel = rollback.DroppedRelations[i];
                if (rel == null
                    || string.IsNullOrEmpty(rel.NewRecordingId)
                    || !string.Equals(rel.OldRecordingId, oldSideId, StringComparison.Ordinal))
                    continue;

                Recording forkRec;
                if (liveRecordingsById != null
                    && liveRecordingsById.TryGetValue(rel.NewRecordingId, out forkRec)
                    && forkRec != null
                    && forkRec.MergeState == MergeState.Immutable)
                {
                    return true;
                }
            }
            return false;
        }
    }
}
