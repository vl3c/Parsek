using System;
using System.Collections.Generic;
using System.Globalization;

namespace Parsek
{
    /// <summary>
    /// Phase 13 of Rewind-to-Staging (design §6.9): single-pass load-time
    /// housekeeping sweep, run from <see cref="ParsekScenario.OnLoad"/>
    /// AFTER <see cref="MergeJournalOrchestrator.RunFinisher"/> and the
    /// Phase 6 rewind-invocation dispatch, and BEFORE
    /// <see cref="RewindPointReaper.ReapOrphanedRPs"/>.
    ///
    /// <para>
    /// The sweep gathers before it deletes so the eligibility decisions are
    /// made against a stable snapshot of the scenario state. Pipeline steps
    /// (design §6.9):
    /// <list type="number">
    ///   <item><description>Marker validation (6 fields).</description></item>
    ///   <item><description>Spare set: session-provisional recordings and
    ///   session-scoped RPs referenced by a valid marker.</description></item>
    ///   <item><description>Discard set: NotCommitted provisional recordings
    ///   with no marker reference.</description></item>
    ///   <item><description>Orphan supersede / tombstone log (kept per §3.5
    ///   invariant 7 / §7.47).</description></item>
    ///   <item><description>Stray <see cref="Recording.SupersedeTargetId"/>
    ///   on Immutable/CommittedProvisional recordings (log Warn + clear).</description></item>
    ///   <item><description>Nested session-provisional cleanup (§7.11):
    ///   when the marker cleared in step 1, every session-scoped RP and
    ///   NotCommitted recording created by that session is discarded.</description></item>
    ///   <item><description>Zombie cleanup: discard-set recordings removed.</description></item>
    ///   <item><description>Load summary log (§10.7).</description></item>
    ///   <item><description>Bump state versions to invalidate caches.</description></item>
    /// </list>
    /// </para>
    /// </summary>
    internal static class LoadTimeSweep
    {
        private const string RewindTag = "Rewind";
        private const string SessionTag = "ReFlySession";
        private const string SupersedeTag = "Supersede";
        private const string LedgerSwapTag = "LedgerSwap";
        private const string RecordingTag = "Recording";
        private const string SweepTag = "LoadSweep";

        /// <summary>
        /// Runs the single-pass gather-then-delete sweep against
        /// <see cref="ParsekScenario.Instance"/>. No-op (with Verbose log)
        /// when the scenario instance is missing.
        /// </summary>
        public static void Run()
        {
            var scenario = ParsekScenario.Instance;
            if (ReferenceEquals(null, scenario))
            {
                ParsekLog.Verbose(SweepTag,
                    "LoadTimeSweep.Run: no ParsekScenario instance — skipping");
                return;
            }

            // ----------------------------------------------------------------
            // Step 1 (§6.9 step 3): validate the marker's six durable fields.
            // On failure, log Warn and clear the marker BEFORE we gather the
            // spare set — §7.11 nested cleanup below depends on the cleared
            // marker's session id.
            // ----------------------------------------------------------------
            var marker = scenario.ActiveReFlySessionMarker;
            var validation = MarkerValidator.Validate(marker);
            bool markerValid = validation.Valid && marker != null;
            string clearedMarkerSessionId = null;

            if (marker != null && !validation.Valid)
            {
                clearedMarkerSessionId = marker.SessionId;
                LogMarkerInvalid(marker, validation.Reason, validation.Details);
                scenario.ActiveReFlySessionMarker = null;
            }
            else if (markerValid)
            {
                var ic = CultureInfo.InvariantCulture;
                ParsekLog.Info(SessionTag,
                    $"Marker valid sess={marker.SessionId ?? "<no-id>"} " +
                    $"tree={marker.TreeId ?? "<no-id>"} " +
                    $"active={marker.ActiveReFlyRecordingId ?? "<no-id>"} " +
                    $"origin={marker.OriginChildRecordingId ?? "<no-id>"} " +
                    $"rp={marker.RewindPointId ?? "<no-id>"} " +
                    $"invokedUT={marker.InvokedUT.ToString("R", ic)} " +
                    $"invokedRealTime={marker.InvokedRealTime ?? "<none>"} " +
                    $"details={validation.Details ?? "<none>"}");
            }

            // ----------------------------------------------------------------
            // Step 2 (§6.9 step 4): gather the spare set — the session-
            // provisional recordings and RPs kept alive by the live marker.
            // ----------------------------------------------------------------
            var spareRecordingIds = new HashSet<string>(StringComparer.Ordinal);
            var spareRpIds = new HashSet<string>(StringComparer.Ordinal);
            if (markerValid)
            {
                if (!string.IsNullOrEmpty(marker.ActiveReFlyRecordingId))
                    spareRecordingIds.Add(marker.ActiveReFlyRecordingId);
                if (!string.IsNullOrEmpty(marker.RewindPointId))
                    spareRpIds.Add(marker.RewindPointId);
                // Any further session-provisional entries carrying the same
                // CreatingSessionId are part of the live session and spared.
                AddSessionScopedEntriesToSpare(
                    scenario, marker.SessionId, spareRecordingIds, spareRpIds);
            }

            // ----------------------------------------------------------------
            // Step 3 (§6.9 step 5): gather discard set — NotCommitted
            // recordings that are NOT in the spare set are zombies from a
            // failed invocation / crashed session.
            // ----------------------------------------------------------------
            var discardRecordings = new List<Recording>();
            var committed = RecordingStore.CommittedRecordings;
            if (committed != null)
            {
                for (int i = 0; i < committed.Count; i++)
                {
                    var rec = committed[i];
                    if (rec == null) continue;
                    if (rec.MergeState != MergeState.NotCommitted) continue;
                    if (rec.RecordingId != null && spareRecordingIds.Contains(rec.RecordingId))
                        continue;
                    discardRecordings.Add(rec);
                }
            }

            // RPs: session-scoped provisionals not in the spare set are dead
            // weight. Normal staging RPs are also born SessionProvisional, but
            // carry no CreatingSessionId; those are durable split points until
            // their owning tree is accepted, so they must survive ordinary scene
            // loads and merge-dialog deferrals.
            var discardRps = new List<RewindPoint>();
            if (scenario.RewindPoints != null)
            {
                for (int i = 0; i < scenario.RewindPoints.Count; i++)
                {
                    var rp = scenario.RewindPoints[i];
                    if (rp == null) continue;
                    if (!rp.SessionProvisional) continue;
                    if (rp.RewindPointId != null && spareRpIds.Contains(rp.RewindPointId))
                        continue;
                    if (!IsSessionScopedProvisionalRp(rp))
                    {
                        ParsekLog.Verbose(RewindTag,
                            $"Keeping session-prov rp={rp.RewindPointId ?? "<no-id>"} " +
                            $"bp={rp.BranchPointId ?? "<no-bp>"} with no session scope");
                        continue;
                    }
                    discardRps.Add(rp);
                }
            }

            // ----------------------------------------------------------------
            // Step 3.5: clean up self-supersede rows (old==new). These are
            // 1-node cycles that poison EffectiveRecordingId's forward walk —
            // the cycle guard returns the recording as its own effective id
            // and logs a WARN on every ERS/ELS rebuild. The caller-side guard
            // (MergeDialog.TryCommitReFlySupersede) and the defense-in-depth
            // guard (SupersedeCommit.AppendRelations) both prevent new ones,
            // but saves written by earlier versions can still contain them.
            // Remove them here so the cycle guard stops firing; the cleaned
            // list persists on the next OnSave through the existing chain
            // (no forced save).
            // ----------------------------------------------------------------
            int selfSupersedes = RemoveSelfSupersedes(scenario);

            // ----------------------------------------------------------------
            // Step 4 (§6.9 step 7): orphan supersede relations (log Warn;
            // kept per §3.5 invariant 7 / §7.47).
            // ----------------------------------------------------------------
            int orphanSupersedes = LogOrphanSupersedes(scenario);

            // ----------------------------------------------------------------
            // Step 5 (§6.9 step 8): orphan tombstones (log Warn; kept).
            // ----------------------------------------------------------------
            int orphanTombstones = LogOrphanTombstones(scenario);

            // ----------------------------------------------------------------
            // Step 6 (§6.9 step 9): stray SupersedeTargetId on non-NotCommitted
            // recordings. Log Warn + clear the transient field.
            // ----------------------------------------------------------------
            int strayFields = ClearStraySupersedeTargets(committed);

            // ----------------------------------------------------------------
            // Step 7 (§7.11): nested session-provisional cleanup. When the
            // marker was cleared above, sweep any session-scoped leftovers
            // the spare-set walk already marked for discard (they are in
            // discardRecordings / discardRps because the cleared marker means
            // spareRecordingIds is empty). This step is observationally a
            // no-op — the gather pass above already picked them up — but we
            // log the count explicitly so the §7.11 contract is visible in
            // KSP.log.
            // ----------------------------------------------------------------
            int nestedCleanupRecordings = 0;
            int nestedCleanupRps = 0;
            if (!string.IsNullOrEmpty(clearedMarkerSessionId))
            {
                nestedCleanupRecordings = CountBySessionId(discardRecordings, clearedMarkerSessionId);
                nestedCleanupRps = CountBySessionId(discardRps, clearedMarkerSessionId);
                if (nestedCleanupRecordings > 0 || nestedCleanupRps > 0)
                {
                    ParsekLog.Info(SessionTag,
                        $"Nested session-prov cleanup sess={clearedMarkerSessionId} " +
                        $"recordings={nestedCleanupRecordings.ToString(CultureInfo.InvariantCulture)} " +
                        $"rps={nestedCleanupRps.ToString(CultureInfo.InvariantCulture)}");
                }
            }

            // ----------------------------------------------------------------
            // Step 8 (§6.9 step 6): delete as one pass.
            // ----------------------------------------------------------------
            int removedRecordings = RemoveDiscardRecordings(discardRecordings);
            int removedRps = RemoveDiscardRps(scenario, discardRps);

            // ----------------------------------------------------------------
            // Step 9 (§6.9 step 10): summary log (§10.7).
            // ----------------------------------------------------------------
            ParsekLog.Info(SweepTag,
                $"[LoadSweep] Marker valid={markerValid.ToString()}; " +
                $"spare={spareRecordingIds.Count.ToString(CultureInfo.InvariantCulture)} " +
                $"discarded={removedRecordings.ToString(CultureInfo.InvariantCulture)} " +
                $"selfSupersedes={selfSupersedes.ToString(CultureInfo.InvariantCulture)} " +
                $"orphanSupersedes={orphanSupersedes.ToString(CultureInfo.InvariantCulture)} " +
                $"orphanTombstones={orphanTombstones.ToString(CultureInfo.InvariantCulture)} " +
                $"strayFields={strayFields.ToString(CultureInfo.InvariantCulture)} " +
                $"discardedRps={removedRps.ToString(CultureInfo.InvariantCulture)}");

            // ----------------------------------------------------------------
            // Step 10 (§6.9 step 11): bump state versions once so every
            // cached ERS / ELS view re-builds on next access.
            // ----------------------------------------------------------------
            scenario.BumpSupersedeStateVersion();
            scenario.BumpTombstoneStateVersion();
            EffectiveState.ResetCachesForTesting();
        }

        // ------------------------------------------------------------------
        // Step 2 helper: session-scoped spare entries.
        // ------------------------------------------------------------------

        private static void AddSessionScopedEntriesToSpare(
            ParsekScenario scenario, string sessionId,
            HashSet<string> spareRecordingIds, HashSet<string> spareRpIds)
        {
            if (string.IsNullOrEmpty(sessionId)) return;

            var committed = RecordingStore.CommittedRecordings;
            if (committed != null)
            {
                for (int i = 0; i < committed.Count; i++)
                {
                    var rec = committed[i];
                    if (rec == null) continue;
                    if (string.IsNullOrEmpty(rec.RecordingId)) continue;
                    if (!string.Equals(rec.CreatingSessionId, sessionId, StringComparison.Ordinal))
                        continue;
                    spareRecordingIds.Add(rec.RecordingId);
                }
            }

            if (scenario.RewindPoints != null)
            {
                for (int i = 0; i < scenario.RewindPoints.Count; i++)
                {
                    var rp = scenario.RewindPoints[i];
                    if (rp == null) continue;
                    if (string.IsNullOrEmpty(rp.RewindPointId)) continue;
                    if (!string.Equals(rp.CreatingSessionId, sessionId, StringComparison.Ordinal))
                        continue;
                    spareRpIds.Add(rp.RewindPointId);
                }
            }
        }

        // ------------------------------------------------------------------
        // Step 3.5 helper: remove self-supersede rows (old==new). These are
        // 1-node cycles left in saves written before the caller-side guard +
        // AppendRelations self-skip landed; they cause EffectiveRecordingId
        // to log a WARN on every ERS/ELS rebuild and keep the superseded
        // recording visible. The cleanup is idempotent (second pass finds 0)
        // and the new list persists on the next OnSave through the regular
        // scenario save chain — no forced save.
        // ------------------------------------------------------------------

        private static int RemoveSelfSupersedes(ParsekScenario scenario)
        {
            if (scenario.RecordingSupersedes == null
                || scenario.RecordingSupersedes.Count == 0)
                return 0;

            int removed = 0;
            // Reverse walk so RemoveAt indices stay valid.
            for (int i = scenario.RecordingSupersedes.Count - 1; i >= 0; i--)
            {
                var rel = scenario.RecordingSupersedes[i];
                if (rel == null) continue;
                if (string.IsNullOrEmpty(rel.OldRecordingId)) continue;
                if (!string.Equals(rel.OldRecordingId, rel.NewRecordingId,
                        StringComparison.Ordinal))
                    continue;

                ParsekLog.Warn(SupersedeTag,
                    $"Self-supersede row rel={rel.RelationId ?? "<no-id>"} " +
                    $"old={rel.OldRecordingId} new={rel.NewRecordingId}; removing (cycle)");
                scenario.RecordingSupersedes.RemoveAt(i);
                removed++;
            }

            if (removed > 0)
            {
                ParsekLog.Info(SweepTag,
                    $"[LoadSweep] Cleaned {removed.ToString(CultureInfo.InvariantCulture)} " +
                    "self-supersede row(s) (old==new); persists on next OnSave");
            }
            return removed;
        }

        // ------------------------------------------------------------------
        // Step 4 helper: orphan supersede relations.
        // ------------------------------------------------------------------

        private static int LogOrphanSupersedes(ParsekScenario scenario)
        {
            if (scenario.RecordingSupersedes == null || scenario.RecordingSupersedes.Count == 0)
                return 0;

            int orphans = 0;
            for (int i = 0; i < scenario.RecordingSupersedes.Count; i++)
            {
                var rel = scenario.RecordingSupersedes[i];
                if (rel == null) continue;

                bool oldResolved = !string.IsNullOrEmpty(rel.OldRecordingId)
                                   && RecordingExists(rel.OldRecordingId);
                bool newResolved = !string.IsNullOrEmpty(rel.NewRecordingId)
                                   && RecordingExists(rel.NewRecordingId);
                if (oldResolved && newResolved) continue;

                ParsekLog.Warn(SupersedeTag,
                    $"Orphan relation={rel.RelationId ?? "<no-id>"} " +
                    $"oldResolved={oldResolved.ToString()} newResolved={newResolved.ToString()} " +
                    $"old={rel.OldRecordingId ?? "<no-id>"} new={rel.NewRecordingId ?? "<no-id>"}");
                orphans++;
            }
            return orphans;
        }

        // ------------------------------------------------------------------
        // Step 5 helper: orphan tombstones.
        // ------------------------------------------------------------------

        private static int LogOrphanTombstones(ParsekScenario scenario)
        {
            if (scenario.LedgerTombstones == null || scenario.LedgerTombstones.Count == 0)
                return 0;

            var actionIds = new HashSet<string>(StringComparer.Ordinal);
            var actions = Ledger.Actions;
            if (actions != null)
            {
                for (int i = 0; i < actions.Count; i++)
                {
                    var a = actions[i];
                    if (a == null) continue;
                    if (!string.IsNullOrEmpty(a.ActionId))
                        actionIds.Add(a.ActionId);
                }
            }

            int orphans = 0;
            for (int i = 0; i < scenario.LedgerTombstones.Count; i++)
            {
                var t = scenario.LedgerTombstones[i];
                if (t == null) continue;
                if (!string.IsNullOrEmpty(t.ActionId) && actionIds.Contains(t.ActionId))
                    continue;

                ParsekLog.Warn(LedgerSwapTag,
                    $"Orphan tombstone={t.TombstoneId ?? "<no-id>"} " +
                    $"actionId={t.ActionId ?? "<no-id>"} " +
                    $"retiring={t.RetiringRecordingId ?? "<no-id>"}");
                orphans++;
            }
            return orphans;
        }

        // ------------------------------------------------------------------
        // Step 6 helper: stray SupersedeTargetId field on committed recordings.
        // ------------------------------------------------------------------

        private static int ClearStraySupersedeTargets(IReadOnlyList<Recording> committed)
        {
            if (committed == null) return 0;
            int strays = 0;
            for (int i = 0; i < committed.Count; i++)
            {
                var rec = committed[i];
                if (rec == null) continue;
                if (string.IsNullOrEmpty(rec.SupersedeTargetId)) continue;
                if (rec.MergeState == MergeState.NotCommitted) continue;

                ParsekLog.Warn(RecordingTag,
                    $"Stray SupersedeTargetId on committed rec={rec.RecordingId ?? "<no-id>"} " +
                    $"state={rec.MergeState} target={rec.SupersedeTargetId}; treating as cleared");
                rec.SupersedeTargetId = null;
                strays++;
            }
            return strays;
        }

        // ------------------------------------------------------------------
        // Step 7 helper: count by session id.
        // ------------------------------------------------------------------

        private static int CountBySessionId(List<Recording> recordings, string sessionId)
        {
            if (recordings == null || string.IsNullOrEmpty(sessionId)) return 0;
            int count = 0;
            for (int i = 0; i < recordings.Count; i++)
            {
                var rec = recordings[i];
                if (rec == null) continue;
                if (string.Equals(rec.CreatingSessionId, sessionId, StringComparison.Ordinal))
                    count++;
            }
            return count;
        }

        private static int CountBySessionId(List<RewindPoint> rps, string sessionId)
        {
            if (rps == null || string.IsNullOrEmpty(sessionId)) return 0;
            int count = 0;
            for (int i = 0; i < rps.Count; i++)
            {
                var rp = rps[i];
                if (rp == null) continue;
                if (string.Equals(rp.CreatingSessionId, sessionId, StringComparison.Ordinal))
                    count++;
            }
            return count;
        }

        private static bool IsSessionScopedProvisionalRp(RewindPoint rp)
        {
            return rp != null
                   && rp.SessionProvisional
                   && !string.IsNullOrEmpty(rp.CreatingSessionId);
        }

        // ------------------------------------------------------------------
        // Step 8 helpers: deletion passes.
        // ------------------------------------------------------------------

        private static int RemoveDiscardRecordings(List<Recording> discard)
        {
            if (discard == null || discard.Count == 0) return 0;
            int removed = 0;
            for (int i = 0; i < discard.Count; i++)
            {
                var rec = discard[i];
                if (rec == null) continue;
                bool ok = RecordingStore.RemoveCommittedInternal(rec);
                if (ok)
                {
                    ParsekLog.Info(RewindTag,
                        $"Zombie discarded rec={rec.RecordingId ?? "<no-id>"} " +
                        $"sess={rec.CreatingSessionId ?? "<no-sess>"} " +
                        $"supersedeTarget={rec.SupersedeTargetId ?? "<none>"}");
                    removed++;
                }
                else
                {
                    ParsekLog.Verbose(RewindTag,
                        $"Zombie recording not found in committed list rec={rec.RecordingId ?? "<no-id>"}");
                }
            }
            ParsekLog.Info(RewindTag,
                $"Zombies discarded={removed.ToString(CultureInfo.InvariantCulture)}");
            return removed;
        }

        private static int RemoveDiscardRps(ParsekScenario scenario, List<RewindPoint> discard)
        {
            if (discard == null || discard.Count == 0) return 0;
            if (scenario.RewindPoints == null) return 0;

            int removed = 0;
            // Build an id set for O(1) membership; walk the scenario list in
            // reverse so RemoveAt indices stay valid.
            var ids = new HashSet<string>(StringComparer.Ordinal);
            for (int i = 0; i < discard.Count; i++)
            {
                var rp = discard[i];
                if (rp?.RewindPointId == null) continue;
                ids.Add(rp.RewindPointId);
            }

            for (int i = scenario.RewindPoints.Count - 1; i >= 0; i--)
            {
                var rp = scenario.RewindPoints[i];
                if (rp == null) continue;
                if (rp.RewindPointId == null || !ids.Contains(rp.RewindPointId)) continue;

                RewindPointReaper.TryDeleteQuicksaveFile(rp);
                scenario.RewindPoints.RemoveAt(i);
                RewindPointReaper.ClearBranchPointBackref(rp);
                ParsekLog.Info(RewindTag,
                    $"Purged session-prov rp={rp.RewindPointId ?? "<no-id>"} " +
                    $"bp={rp.BranchPointId ?? "<no-bp>"} " +
                    $"sess={rp.CreatingSessionId ?? "<no-sess>"}");
                removed++;
            }

            ParsekLog.Info(RewindTag,
                $"Purged session-prov RPs={removed.ToString(CultureInfo.InvariantCulture)}");
            return removed;
        }

        // ------------------------------------------------------------------
        // Shared helpers.
        // ------------------------------------------------------------------

        private static bool RecordingExists(string recordingId)
        {
            if (string.IsNullOrEmpty(recordingId)) return false;
            var committed = RecordingStore.CommittedRecordings;
            if (committed == null) return false;
            for (int i = 0; i < committed.Count; i++)
            {
                var rec = committed[i];
                if (rec == null) continue;
                if (string.Equals(rec.RecordingId, recordingId, StringComparison.Ordinal))
                    return true;
            }
            return false;
        }

        private static void LogMarkerInvalid(ReFlySessionMarker marker, string reason, string details)
        {
            var ic = CultureInfo.InvariantCulture;
            ParsekLog.Warn(SessionTag,
                $"Marker invalid field={reason ?? "<unknown>"}; cleared " +
                $"sess={marker.SessionId ?? "<no-id>"} " +
                $"tree={marker.TreeId ?? "<no-id>"} " +
                $"active={marker.ActiveReFlyRecordingId ?? "<no-id>"} " +
                $"origin={marker.OriginChildRecordingId ?? "<no-id>"} " +
                $"rp={marker.RewindPointId ?? "<no-id>"} " +
                $"invokedUT={marker.InvokedUT.ToString("R", ic)} " +
                $"invokedRealTime={marker.InvokedRealTime ?? "<none>"} " +
                $"details={details ?? "<none>"}");
        }
    }
}
