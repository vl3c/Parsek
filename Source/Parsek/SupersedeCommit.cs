using System;
using System.Collections.Generic;
using System.Globalization;

namespace Parsek
{
    /// <summary>
    /// Phase 8 of Rewind-to-Staging (design §5.3 / §5.5 / §6.6 step 2-3 /
    /// §7.17 / §7.43 / §10.4): commits a re-fly session's supersede relations
    /// for the subtree rooted at the marker's <c>OriginChildRecordingId</c>
    /// and flips the provisional's <see cref="MergeState"/> to either
    /// <see cref="MergeState.Immutable"/> (Landed / stable) or
    /// <see cref="MergeState.CommittedProvisional"/> (Crashed — still
    /// re-flyable).
    ///
    /// <para>
    /// This commit step is what "hides the origin subtree from ERS" — it
    /// appends one <see cref="RecordingSupersedeRelation"/> per recording in
    /// the closure, all pointing at the provisional's id. The closure is the
    /// same forward-only, merge-guarded walk used by the physical-visibility
    /// layer (<see cref="EffectiveState.ComputeSessionSuppressedSubtree"/>);
    /// after commit, the ERS cache invalidates on the
    /// <see cref="ParsekScenario.BumpSupersedeStateVersion"/> bump and the
    /// subtree flips from "session-suppressed" to "superseded" — same
    /// invisibility, different mechanism.
    /// </para>
    ///
    /// <para>
    /// Phase 8 does NOT write a <see cref="MergeJournal"/>; the journaled
    /// staged commit lands in Phase 10. If this call crashes mid-way, Phase
    /// 13's load-time sweep will clean up the dangling marker + partial
    /// supersede relations.
    /// </para>
    /// </summary>
    internal static class SupersedeCommit
    {
        private const string Tag = "Supersede";
        private const string SessionTag = "ReFlySession";

        /// <summary>
        /// Idempotent: appends supersede relations for every id in the
        /// forward-only merge-guarded subtree closure of
        /// <paramref name="marker"/>, flips
        /// <paramref name="provisional"/>.<see cref="Recording.MergeState"/>,
        /// clears <see cref="Recording.SupersedeTargetId"/>, and clears
        /// <see cref="ParsekScenario.ActiveReFlySessionMarker"/>. Callers
        /// (currently <see cref="MergeDialog.MergeCommit"/>) invoke this
        /// AFTER the tree has committed and BEFORE firing
        /// <see cref="MergeDialog.OnTreeCommitted"/>.
        /// </summary>
        internal static void CommitSupersede(ReFlySessionMarker marker, Recording provisional)
        {
            if (marker == null)
            {
                ParsekLog.Warn(Tag, "CommitSupersede: marker is null — nothing to commit");
                return;
            }
            if (provisional == null)
            {
                ParsekLog.Warn(Tag, "CommitSupersede: provisional is null — nothing to commit");
                return;
            }

            var scenario = ParsekScenario.Instance;
            if (object.ReferenceEquals(null, scenario))
            {
                ParsekLog.Warn(Tag, "CommitSupersede: no ParsekScenario instance — nothing to commit");
                return;
            }

            if (scenario.RecordingSupersedes == null)
                scenario.RecordingSupersedes = new List<RecordingSupersedeRelation>();

            // Step 1: compute the forward-only merge-guarded subtree closure
            // rooted at the marker's origin child recording. Reuses the exact
            // walk that SessionSuppressionState/ERS caching relies on so the
            // visible-after-commit subtree is identical to the invisible-
            // during-session subtree.
            IReadOnlyCollection<string> subtree =
                EffectiveState.ComputeSessionSuppressedSubtree(marker);
            int subtreeCount = subtree?.Count ?? 0;

            string newRecordingId = provisional.RecordingId;
            string originId = marker.OriginChildRecordingId;

            // Step 2: for each id in the subtree, append a supersede relation
            // if one doesn't already exist (defensive idempotence).
            int added = 0;
            int skippedExisting = 0;
            double ut = SafeNow();
            string nowIso = DateTime.UtcNow.ToString("o");
            var ic = CultureInfo.InvariantCulture;

            if (subtree != null)
            {
                foreach (string oldId in subtree)
                {
                    if (string.IsNullOrEmpty(oldId)) continue;
                    if (RelationExists(scenario.RecordingSupersedes, oldId, newRecordingId))
                    {
                        skippedExisting++;
                        ParsekLog.Verbose(Tag,
                            $"CommitSupersede: skip existing relation old={oldId} new={newRecordingId}");
                        continue;
                    }
                    var rel = new RecordingSupersedeRelation
                    {
                        RelationId = "rsr_" + Guid.NewGuid().ToString("N"),
                        OldRecordingId = oldId,
                        NewRecordingId = newRecordingId,
                        UT = ut,
                        CreatedRealTime = nowIso,
                    };
                    scenario.RecordingSupersedes.Add(rel);
                    added++;
                    ParsekLog.Info(Tag,
                        $"rel={rel.RelationId} old={rel.OldRecordingId} new={rel.NewRecordingId}");
                }
            }

            ParsekLog.Info(Tag,
                $"Added {added.ToString(ic)} supersede relations for subtree rooted at {originId ?? "<none>"} " +
                $"(subtreeCount={subtreeCount.ToString(ic)} skippedExisting={skippedExisting.ToString(ic)})");

            // Step 3: classify terminal kind and flip MergeState. Crashed outcomes
            // stay re-flyable (CommittedProvisional); anything else is Immutable.
            TerminalKind kind = TerminalKindClassifier.Classify(provisional);
            MergeState newState = (kind == TerminalKind.Crashed)
                ? MergeState.CommittedProvisional
                : MergeState.Immutable;
            provisional.MergeState = newState;

            // Step 4: clear the transient SupersedeTargetId (§5.5).
            string priorTarget = provisional.SupersedeTargetId;
            provisional.SupersedeTargetId = null;

            // Step 5: bump supersede version so ERS cache invalidates and the
            // superseded subtree disappears immediately.
            scenario.BumpSupersedeStateVersion();

            ParsekLog.Info(Tag,
                $"provisional={newRecordingId ?? "<no-id>"} mergeState={newState} terminalKind={kind} " +
                $"priorTarget={priorTarget ?? "<none>"}");

            // Step 6: clear the active marker. The session is finished; Phase
            // 7's SessionSuppressionState will emit the End transition log on
            // the next query.
            string sessionId = marker.SessionId;
            scenario.ActiveReFlySessionMarker = null;
            // Bumping again ensures the ERS cache that keyed on the old marker
            // identity rebuilds; redundant with step 5 in most paths but cheap
            // and defensive against future ordering changes.
            scenario.BumpSupersedeStateVersion();

            ParsekLog.Info(SessionTag,
                $"End reason=merged sess={sessionId ?? "<no-id>"} provisional={newRecordingId ?? "<no-id>"}");
        }

        private static bool RelationExists(
            List<RecordingSupersedeRelation> relations, string oldId, string newId)
        {
            if (relations == null || relations.Count == 0) return false;
            for (int i = 0; i < relations.Count; i++)
            {
                var rel = relations[i];
                if (rel == null) continue;
                if (string.Equals(rel.OldRecordingId, oldId, StringComparison.Ordinal)
                    && string.Equals(rel.NewRecordingId, newId, StringComparison.Ordinal))
                {
                    return true;
                }
            }
            return false;
        }

        private static double SafeNow()
        {
            try
            {
                return Planetarium.GetUniversalTime();
            }
            catch
            {
                // Unit tests outside Unity: Planetarium is unavailable.
                return 0.0;
            }
        }
    }
}
