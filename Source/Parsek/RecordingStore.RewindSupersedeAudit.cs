using System;
using System.Collections.Generic;

namespace Parsek
{
    public static partial class RecordingStore
    {
        /// <summary>
        /// What a plain rewind found in the rewound tree's supersede rows. A rewind moves
        /// the clock and nothing else (owner ruling 2026-10-24), so every row is kept:
        /// this is a read-only audit for the log, never an input to a mutation.
        /// </summary>
        internal struct RewindSupersedeAudit
        {
            /// <summary>Rows in scope: Old in the rewound subtree (or New is the owner)
            /// and the fork starting at or after the rewind UT.</summary>
            public int Rows;
            /// <summary>Distinct forks those rows name.</summary>
            public int Forks;
            /// <summary>Forks that are committed <see cref="MergeState.Immutable"/>.</summary>
            public int Sealed;
            /// <summary>Forks that are committed <see cref="MergeState.CommittedProvisional"/>.</summary>
            public int Open;
            /// <summary>Forks missing from the committed set or still
            /// <see cref="MergeState.NotCommitted"/>. Kept like the others: a one-sided row
            /// keeps hiding its old recording, as it does on every load.</summary>
            public int Dangling;
            /// <summary>The distinct fork ids, in row order.</summary>
            public List<string> ForkIds;
        }

        /// <summary>
        /// Classifies the supersede rows a plain rewind of <paramref name="owner"/> reaches,
        /// without changing any of them. In scope: rows whose <c>OldRecordingId</c> is the
        /// owner or a recording of the owner's tree starting at or after
        /// <paramref name="rewindAdjustedUT"/>, or whose <c>NewRecordingId</c> is the owner,
        /// and whose fork starts at or after the rewind (the fork's <c>StartUT</c>, or the
        /// row's own <c>UT</c> when the fork is not in <paramref name="liveRecordingsById"/>).
        ///
        /// <para>Every row stays, whatever its fork's state. A sealed fork stays sealed, an
        /// open fork stays open and re-flyable, and a row whose fork is missing (its
        /// recording failed to load) keeps hiding the replaced recording exactly as the
        /// load-time sweep keeps it: the ledger still holds the re-fly's rows and the
        /// replaced flight's tombstones (owner ruling O3: a recording that fails to load
        /// keeps its ledger rows), so bringing the replaced flight back would show a
        /// timeline the career does not match, and could reopen a slot whose re-fly
        /// already counted.</para>
        /// </summary>
        internal static RewindSupersedeAudit AuditSupersedesKeptAcrossRewindPure(
            Recording owner,
            double rewindAdjustedUT,
            IReadOnlyList<Recording> ownerTreeRecordings,
            IReadOnlyDictionary<string, Recording> liveRecordingsById,
            IReadOnlyList<RecordingSupersedeRelation> supersedes)
        {
            var audit = new RewindSupersedeAudit { ForkIds = new List<string>() };
            if (owner == null || supersedes == null || supersedes.Count == 0)
                return audit;

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

            var seenForks = new HashSet<string>(StringComparer.Ordinal);
            for (int i = 0; i < supersedes.Count; i++)
            {
                var rel = supersedes[i];
                if (rel == null || string.IsNullOrEmpty(rel.OldRecordingId)) continue;
                if (string.IsNullOrEmpty(rel.NewRecordingId)) continue;

                bool oldInScope = rewoundOutOldIds.Contains(rel.OldRecordingId);
                bool newIsOwner = !string.IsNullOrEmpty(owner.RecordingId)
                    && string.Equals(rel.NewRecordingId, owner.RecordingId, StringComparison.Ordinal);
                if (!oldInScope && !newIsOwner) continue;

                Recording newRec = null;
                if (liveRecordingsById != null)
                    liveRecordingsById.TryGetValue(rel.NewRecordingId, out newRec);
                double effectiveForkUT = newRec != null ? newRec.StartUT : rel.UT;
                if (effectiveForkUT < rewindAdjustedUT) continue;

                audit.Rows++;
                if (!seenForks.Add(rel.NewRecordingId)) continue;

                audit.Forks++;
                audit.ForkIds.Add(rel.NewRecordingId);
                if (newRec == null || newRec.MergeState == MergeState.NotCommitted)
                    audit.Dangling++;
                else if (newRec.MergeState == MergeState.Immutable)
                    audit.Sealed++;
                else
                    audit.Open++;
            }
            return audit;
        }
    }
}
