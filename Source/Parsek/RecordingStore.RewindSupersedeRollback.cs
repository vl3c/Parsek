using System;
using System.Collections.Generic;

namespace Parsek
{
    public static partial class RecordingStore
    {
        /// <summary>
        /// At rewind time, drops <see cref="RecordingSupersedeRelation"/> rows whose
        /// fork (<c>NewRecordingId</c>) starts at or after <paramref name="rewindAdjustedUT"/>
        /// AND whose source (<c>OldRecordingId</c>) belongs to the rewound owner's tree.
        /// Walks the entire tree of the rewound owner so branch recordings (e.g. an
        /// upper-stage Probe at index #7) are also unsuppressed — without this, only
        /// the owner's row drops and the branch ghosts stay
        /// <c>reason=superseded-by-relation</c> after the user re-launches.
        ///
        /// Pure-static: takes the supersede list as a parameter so it's directly unit
        /// testable without Unity. Returns the number of relations dropped.
        ///
        /// Multi-generational chains (A → B → C with A as the rewound owner) collapse
        /// correctly: <paramref name="ownerTreeRecordings"/> includes B (and B's
        /// StartUT >= rewindUT, so B is in <c>rewoundOutOldIds</c>), so both A→B
        /// and B→C drop.
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

            // Two-pass classification of in-scope supersede relations:
            //
            //   Pass 1 — for every relation whose OldRecordingId belongs to the
            //     rewound subtree AND whose fork is "rewound out" (preferred
            //     signal: Recording.StartUT; orphan fallback: rel.UT — the merge
            //     time recorded on the relation itself, so one-sided orphan rows
            //     don't silently keep suppressing OldRecordingId after rewind):
            //       - if the fork's MergeState is Immutable → tentatively
            //         preserve (canon fork survives parent rewind);
            //       - otherwise → drop + retire (existing behaviour).
            //
            //   Pass 2 — demote any tentative preservation whose OldRecordingId
            //     is itself being retired in this same batch. The canon fork's
            //     priorTip is gone, so the canon has no live source to be canon
            //     over; preserving it would leave A and C both visible alongside
            //     the restored A — the same double-materialization regression
            //     that PR #776/#777 fixed (see
            //     docs/dev/plans/fix-watch-double-probe-ghost-after-rewind.md).
            //
            // Coupling hazard: ParsekScenario.MarkRewoundTreeRecordingsAsGhostOnly
            // currently scopes SpawnSuppressedByRewind to the active/source
            // recording only (per #589). If that scope ever expands to cover
            // same-tree future recordings, the preserved canon fork would be
            // silently re-suppressed at spawn time even though this predicate
            // kept its supersede relation. Watch that helper during review of
            // any future Rewind-scope changes.
            var pendingDrops = new List<RecordingSupersedeRelation>();
            var pendingImmutablePreservations = new List<RecordingSupersedeRelation>();
            for (int i = 0; i < supersedes.Count; i++)
            {
                var rel = supersedes[i];
                if (rel == null || string.IsNullOrEmpty(rel.OldRecordingId)) continue;
                if (string.IsNullOrEmpty(rel.NewRecordingId)) continue;

                // Two distinct in-scope cases:
                //
                //   (a) "Outgoing": rel.OldRecordingId is in the rewound subtree
                //       (parent rewind on A drops every A→fork relation). The
                //       canonical case: parent A is rewound, all A's forks must
                //       go (or preserve, if Immutable).
                //
                //   (b) "Incoming, self-rewind on canon": rel.NewRecordingId IS
                //       the rewind owner. The user clicked Rewind on the canon
                //       fork itself — they want to undo this canon recording.
                //       The incoming relation (priorTip → canon) must drop so
                //       priorTip becomes visible again. Without this, A→B(Imm)
                //       with self-rewind on B leaves A→B intact, B's recording
                //       is rewound away but A stays hidden by the orphaned
                //       relation.
                //
                // Cases (a) and (b) overlap in the parent-rewind subtree case
                // (both Old and New are in rewoundOutOldIds). We distinguish
                // case (b) explicitly here so it bypasses the Immutable
                // preservation branch — self-rewind on a canon fork must drop
                // the relation regardless of MergeState, because the user is
                // explicitly undoing this canon. Outgoing relations from the
                // canon-fork-as-owner already drop via case (a) — see
                // Rollback_OwnerIsImmutableFork_RewindOnSelfStillDrops.
                bool oldInScope = rewoundOutOldIds.Contains(rel.OldRecordingId);
                bool newIsSelfRewind = !string.IsNullOrEmpty(owner.RecordingId)
                    && string.Equals(rel.NewRecordingId, owner.RecordingId, StringComparison.Ordinal);
                if (!oldInScope && !newIsSelfRewind) continue;

                Recording newRec = null;
                if (liveRecordingsById != null)
                    liveRecordingsById.TryGetValue(rel.NewRecordingId, out newRec);
                double effectiveForkUT = newRec != null ? newRec.StartUT : rel.UT;
                if (effectiveForkUT < rewindAdjustedUT) continue;

                // Self-rewind on the canon (case b): force drop. The Immutable
                // preservation contract applies only to parent rewind — when
                // the user explicitly rewinds the canon fork itself, the
                // canon is being undone, not preserved.
                //
                // Track the forced-drop new-id so EnsureRewindRetirementsForRollback's
                // defensive Immutable guard can let this retirement proceed
                // (instead of re-inserting the relation we just chose to drop).
                if (newIsSelfRewind)
                {
                    pendingDrops.Add(rel);
                    if (!string.IsNullOrEmpty(rel.NewRecordingId))
                        result.ForcedSelfRewindDropIds.Add(rel.NewRecordingId);
                    continue;
                }

                // Orphan-fallback (newRec == null) cannot read MergeState — drop
                // as today; the fork is gone anyway and the relation would
                // otherwise dangle and silently suppress OldRecordingId.
                if (newRec != null && newRec.MergeState == MergeState.Immutable)
                    pendingImmutablePreservations.Add(rel);
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
            // Cascade example: A → B(Provisional) → C(Immutable) → D(Immutable),
            // rewind past A's start.
            //   Initial: pendingDrops=[A→B], pendingImmutablePreservations=[B→C, C→D].
            //   Pass 2 iter 1: B in pendingRetiredNewIds={B} → B→C demotes;
            //     pendingRetiredNewIds becomes {B,C}.
            //   Pass 2 iter 2: C now in pendingRetiredNewIds → C→D demotes too;
            //     pendingRetiredNewIds becomes {B,C,D}.
            //   Pass 2 iter 3: no more preservations → terminate.
            // Without the fixpoint loop, C→D would survive the demotion pass
            // and D would render as canon alongside the restored A — the
            // double-materialization regression PR #776/#777 fixes.
            if (pendingImmutablePreservations.Count > 0)
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
                    for (int i = pendingImmutablePreservations.Count - 1; i >= 0; i--)
                    {
                        var rel = pendingImmutablePreservations[i];
                        if (!string.IsNullOrEmpty(rel.OldRecordingId)
                            && pendingRetiredNewIds.Contains(rel.OldRecordingId))
                        {
                            pendingImmutablePreservations.RemoveAt(i);
                            pendingDrops.Add(rel);
                            if (!string.IsNullOrEmpty(rel.NewRecordingId))
                            {
                                result.DemotedImmutablePreservationIds.Add(rel.NewRecordingId);
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

                // Anything still in pendingImmutablePreservations is a
                // confirmed canon preservation.
                for (int i = 0; i < pendingImmutablePreservations.Count; i++)
                {
                    string newId = pendingImmutablePreservations[i].NewRecordingId;
                    if (!string.IsNullOrEmpty(newId))
                        result.SkippedImmutableForkRecordingIds.Add(newId);
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

            // Also prune any preserved canon ids — a recording can be both
            // the Old of a dropped relation (added to RestoredRecordingIds
            // by the apply loop above) AND the New of a preserved Immutable
            // relation (in SkippedImmutableForkRecordingIds, hence the canon
            // head). Example: A → B(Imm) → C(Prov) → D(Imm) rewind past A.
            // B preserves as canon (A→B kept) but is also the priorTip of
            // the dropped B→C, so it lands in RestoredRecordingIds. The
            // live caller's Pass 2 (PR #807 old-side retirement) iterates
            // RestoredRecordingIds and would retire B — leaving NO canon
            // head visible (A hidden by surviving A→B, B retired by old-side
            // pass). Canon preservation must win: anything in
            // SkippedImmutableForkRecordingIds is the canon and stays
            // visible, never a candidate for old-side retirement.
            foreach (string preservedId in result.SkippedImmutableForkRecordingIds)
                result.RestoredRecordingIds.Remove(preservedId);

            return result;
        }

        /// <summary>
        /// Returns true when at least one dropped relation targeting
        /// <paramref name="oldSideId"/> as its OldRecordingId has a fork
        /// (NewRecordingId) resolving to an <see cref="MergeState.Immutable"/>
        /// recording AND was not flagged as a forced self-rewind drop. That
        /// configuration represents a permanent (canon) supersede the rewind
        /// erased, so the priorTip stays retired. Any other configuration —
        /// non-Immutable fork, forced self-rewound canon, or missing fork —
        /// returns false so the priorTip can become visible again.
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

                // Forced self-rewind on the canon means the user explicitly
                // undid the canon recording; the priorTip should become the
                // new visible state, not stay retired.
                if (rollback.ForcedSelfRewindDropIds != null
                    && rollback.ForcedSelfRewindDropIds.Contains(rel.NewRecordingId))
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
