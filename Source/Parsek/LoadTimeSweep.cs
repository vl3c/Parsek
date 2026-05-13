using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;

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
    ///   <item><description>One-sided orphan supersede / tombstone log
    ///   (kept per §3.5 invariant 7 / §7.47); fully orphaned supersedes are
    ///   removed because neither endpoint can affect effective state.</description></item>
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
            // Step 1 (§6.9 step 3): validate the marker's durable fields.
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
                Parsek.Rendering.RenderSessionState.Clear("marker-cleared");
                // Sweep runs during OnLoad before flight-scene Start, so
                // FlightDriver state is unset and Apply is a logged no-op.
                // Defensive: keep the call symmetric with other clear sites
                // so the gate's forcedFlag tracking stays consistent if
                // OnLoad fires inside an already-loaded flight scene.
                ReFlyRevertButtonGate.Apply("LoadTimeSweep:invalid-marker");
                // #688 follow-up: snapshots tagged with the cleared session
                // id are picked up by SweepOrphanPreReFlyAnchorSnapshots at
                // the end of this same Sweep pass — no targeted clear here
                // (it would double-log the same recording). The orphan
                // sweep treats any snapshot whose sessionId differs from
                // the live marker (now null) as orphan and clears it.
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

            // Supersede orphan sweep runs after zombie recording deletion below
            // so rows whose endpoints are removed by this same load pass are
            // classified against the final post-sweep recording set.
            SupersedeOrphanSweepResult orphanSupersedes = new SupersedeOrphanSweepResult();

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
            // Step 8.5 (§6.9 step 7): one-sided orphan supersede relations
            // log Warn and stay in place; fully orphaned rows cannot affect
            // effective recording resolution, so they are removed to stop
            // warning on every load/save cycle.
            // ----------------------------------------------------------------
            orphanSupersedes = SweepOrphanSupersedes(scenario);
            // Step 10 below bumps SupersedeStateVersion once for the whole sweep,
            // covering rewind-retirement removals without a second local bump.
            int orphanRewindRetirements = SweepOrphanRewindRetirements(scenario);
            if (markerValid)
            {
                ParsekLog.Verbose("GroupHierarchy",
                    "Skipping group hierarchy prune reason=load-time-sweep while Re-Fly session is active");
            }
            else
            {
                GroupHierarchyStore.PruneUnusedHierarchyEntriesFromCommittedRecordings("load-time-sweep");
            }
            int missingQuicksaveRps = SweepMissingRewindPointQuicksaves(scenario);

            // ----------------------------------------------------------------
            // Step 8.6 (#688 follow-up): defensive backstop for orphan
            // pre-Re-Fly anchor snapshots. The expected lifecycle clears
            // each snapshot when its session ends (merge / retry / discard
            // / invalid-marker branch above). This sweep catches any
            // snapshot whose session id no longer matches the live marker
            // — e.g. a future code path that clears the marker without
            // calling SupersedeCommit.ClearPreReFlyAnchorSnapshotsForSession,
            // or a save that interleaved a marker clear with a snapshot
            // leftover. Keeps the persisted PRE_REFLY_ANCHOR ConfigNode
            // bounded to active sessions only.
            // ----------------------------------------------------------------
            int orphanSnapshotsCleared = SweepOrphanPreReFlyAnchorSnapshots(scenario);

            // ----------------------------------------------------------------
            // Step 9 (§6.9 step 10): summary log (§10.7).
            // ----------------------------------------------------------------
            ParsekLog.Info(SweepTag,
                $"[LoadSweep] Marker valid={markerValid.ToString()}; " +
                $"spare={spareRecordingIds.Count.ToString(CultureInfo.InvariantCulture)} " +
                $"discarded={removedRecordings.ToString(CultureInfo.InvariantCulture)} " +
                $"selfSupersedes={selfSupersedes.ToString(CultureInfo.InvariantCulture)} " +
                $"orphanSupersedes={orphanSupersedes.RetainedOrphans.ToString(CultureInfo.InvariantCulture)} " +
                $"removedFullyOrphanSupersedes={orphanSupersedes.RemovedFullyOrphaned.ToString(CultureInfo.InvariantCulture)} " +
                $"removedOrphanRewindRetirements={orphanRewindRetirements.ToString(CultureInfo.InvariantCulture)} " +
                $"orphanTombstones={orphanTombstones.ToString(CultureInfo.InvariantCulture)} " +
                $"strayFields={strayFields.ToString(CultureInfo.InvariantCulture)} " +
                $"discardedRps={removedRps.ToString(CultureInfo.InvariantCulture)} " +
                $"missingQuicksaveRps={missingQuicksaveRps.ToString(CultureInfo.InvariantCulture)} " +
                $"orphanReFlyAnchors={orphanSnapshotsCleared.ToString(CultureInfo.InvariantCulture)}");

            // ----------------------------------------------------------------
            // Step 10 (§6.9 step 11): bump state versions once so every
            // cached ERS / ELS view re-builds on next access.
            // ----------------------------------------------------------------
            scenario.BumpSupersedeStateVersion();
            scenario.BumpTombstoneStateVersion();
            EffectiveState.ResetCachesForTesting();
        }

        internal static int SweepMissingRewindPointQuicksaves(ParsekScenario scenario)
        {
            if (object.ReferenceEquals(null, scenario)
                || scenario.RewindPoints == null
                || scenario.RewindPoints.Count == 0)
                return 0;

            string markerRewindPointId = scenario.ActiveReFlySessionMarker?.RewindPointId;
            var missing = new List<MissingRewindPointQuicksave>();
            for (int i = 0; i < scenario.RewindPoints.Count; i++)
            {
                var rp = scenario.RewindPoints[i];
                if (rp == null || string.IsNullOrEmpty(rp.RewindPointId))
                    continue;

                string absolutePath = RewindPointReaper.ResolveQuicksaveAbsolutePath(rp.RewindPointId);
                if (string.IsNullOrEmpty(absolutePath))
                    continue;
                if (File.Exists(absolutePath))
                    continue;
                // Mirror RewindPointReaper's active-marker guard: the live
                // Re-Fly session still owns this RP, so load-time recovery must
                // not seal its slots out from under the in-flight marker.
                if (!string.IsNullOrEmpty(markerRewindPointId)
                    && string.Equals(rp.RewindPointId, markerRewindPointId, StringComparison.Ordinal))
                {
                    ParsekLog.Verbose(SweepTag,
                        $"Missing rewind-point quicksave sweep skipped active marker rp={rp.RewindPointId}");
                    continue;
                }
                // ReapOrphanedRPs always retains session-provisional RPs. The
                // missing-file sweep must not seal slots on an RP the reaper
                // is guaranteed to leave in scenario state.
                if (rp.SessionProvisional)
                {
                    ParsekLog.Verbose(SweepTag,
                        $"Missing rewind-point quicksave sweep skipped session-provisional rp={rp.RewindPointId} " +
                        $"sess={rp.CreatingSessionId ?? "<no-sess>"}");
                    continue;
                }

                if (rp.ChildSlots != null)
                {
                    for (int s = 0; s < rp.ChildSlots.Count; s++)
                    {
                        var slot = rp.ChildSlots[s];
                        if (slot == null) continue;
                        slot.Sealed = true;
                    }
                }

                missing.Add(new MissingRewindPointQuicksave(
                    rp.RewindPointId,
                    rp.BranchPointId,
                    rp.ChildSlots?.Count ?? 0,
                    absolutePath));
            }

            if (missing.Count == 0)
                return 0;

            RewindPointReaper.ReapOrphanedRPs();

            var remainingRpIds = new HashSet<string>(StringComparer.Ordinal);
            for (int i = 0; i < scenario.RewindPoints.Count; i++)
            {
                var rp = scenario.RewindPoints[i];
                if (rp == null || string.IsNullOrEmpty(rp.RewindPointId))
                    continue;
                remainingRpIds.Add(rp.RewindPointId);
            }

            int cleaned = 0;
            for (int i = 0; i < missing.Count; i++)
            {
                var entry = missing[i];
                if (string.IsNullOrEmpty(entry.RewindPointId)
                    || remainingRpIds.Contains(entry.RewindPointId))
                    continue;

                cleaned++;
                ParsekLog.Info(SweepTag,
                    $"Cleaned missing rewind-point quicksave: rp={entry.RewindPointId} " +
                    $"bp={entry.BranchPointId ?? "<no-bp>"} " +
                    $"slots={entry.SlotCount.ToString(CultureInfo.InvariantCulture)} " +
                    $"path={entry.AbsolutePath}");
            }

            return cleaned;
        }

        private sealed class MissingRewindPointQuicksave
        {
            public MissingRewindPointQuicksave(
                string rewindPointId,
                string branchPointId,
                int slotCount,
                string absolutePath)
            {
                RewindPointId = rewindPointId;
                BranchPointId = branchPointId;
                SlotCount = slotCount;
                AbsolutePath = absolutePath;
            }

            public string RewindPointId { get; }
            public string BranchPointId { get; }
            public int SlotCount { get; }
            public string AbsolutePath { get; }
        }

        /// <summary>
        /// Defensive backstop: clears any pre-Re-Fly anchor snapshot whose
        /// <see cref="Recording.PreReFlyAnchorSessionId"/> doesn't match the
        /// live <see cref="ParsekScenario.ActiveReFlySessionMarker"/>'s
        /// session id. The expected lifecycle clears snapshots through the
        /// session-end paths
        /// (<see cref="SupersedeCommit.ClearPreReFlyAnchorSnapshotsForSession"/>);
        /// any leftover here means a session-end path missed the clear or
        /// a save interleaved a marker mutation with a snapshot leftover.
        /// Returns the number of snapshots cleared.
        /// </summary>
        private static int SweepOrphanPreReFlyAnchorSnapshots(ParsekScenario scenario)
        {
            string liveSessionId = scenario?.ActiveReFlySessionMarker?.SessionId;
            int cleared = 0;
            var committed = RecordingStore.CommittedRecordings;
            if (committed == null) return 0;
            for (int i = 0; i < committed.Count; i++)
            {
                var rec = committed[i];
                if (rec == null) continue;
                if (string.IsNullOrEmpty(rec.PreReFlyAnchorSessionId)) continue;
                if (!string.IsNullOrEmpty(liveSessionId)
                    && string.Equals(rec.PreReFlyAnchorSessionId, liveSessionId, StringComparison.Ordinal))
                    continue;
                // Per CLAUDE.md batch counting convention: per-item events at
                // Verbose, single Warn summary after the loop when cleared > 0.
                ParsekLog.Verbose(SessionTag,
                    $"Stray pre-Re-Fly anchor snapshot on rec={rec.RecordingId ?? "<no-id>"}: " +
                    $"sess={rec.PreReFlyAnchorSessionId} (live sess={liveSessionId ?? "<none>"}) - clearing");
                rec.ClearPreReFlyAnchorTrajectory();
                cleared++;
            }
            if (cleared > 0)
            {
                ParsekLog.Warn(SessionTag,
                    $"Cleared {cleared.ToString(CultureInfo.InvariantCulture)} stray pre-Re-Fly anchor " +
                    $"snapshot(s) at load time (live sess={liveSessionId ?? "<none>"})");
            }
            return cleared;
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

        private struct SupersedeOrphanSweepResult
        {
            public int RetainedOrphans;
            public int RemovedFullyOrphaned;
        }

        private static SupersedeOrphanSweepResult SweepOrphanSupersedes(ParsekScenario scenario)
        {
            var result = new SupersedeOrphanSweepResult();
            if (scenario.RecordingSupersedes == null || scenario.RecordingSupersedes.Count == 0)
                return result;

            var knownRecordingIds = RecordingStore.BuildKnownRecordingIdsForCleanup();
            for (int i = scenario.RecordingSupersedes.Count - 1; i >= 0; i--)
            {
                var rel = scenario.RecordingSupersedes[i];
                if (rel == null) continue;

                bool oldResolved = !string.IsNullOrEmpty(rel.OldRecordingId)
                                   && RecordingExists(rel.OldRecordingId, knownRecordingIds);
                bool newResolved = !string.IsNullOrEmpty(rel.NewRecordingId)
                                   && RecordingExists(rel.NewRecordingId, knownRecordingIds);
                if (oldResolved && newResolved) continue;

                if (!oldResolved && !newResolved)
                {
                    ParsekLog.Info(SupersedeTag,
                        $"Fully orphaned relation={rel.RelationId ?? "<no-id>"} " +
                        $"old={rel.OldRecordingId ?? "<no-id>"} new={rel.NewRecordingId ?? "<no-id>"}; removing");
                    scenario.RecordingSupersedes.RemoveAt(i);
                    result.RemovedFullyOrphaned++;
                    continue;
                }

                ParsekLog.Warn(SupersedeTag,
                    $"Orphan relation={rel.RelationId ?? "<no-id>"} " +
                    $"oldResolved={oldResolved.ToString()} newResolved={newResolved.ToString()} " +
                    $"old={rel.OldRecordingId ?? "<no-id>"} new={rel.NewRecordingId ?? "<no-id>"}");
                result.RetainedOrphans++;
            }
            return result;
        }

        private static int SweepOrphanRewindRetirements(ParsekScenario scenario)
        {
            if (object.ReferenceEquals(null, scenario)
                || scenario.RecordingRewindRetirements == null
                || scenario.RecordingRewindRetirements.Count == 0)
                return 0;

            var knownRecordingIds = RecordingStore.BuildKnownRecordingIdsForCleanup();

            // Build an Immutable-id lookup so we can detect retirements pointing
            // at canon recordings. Pre-fix saves can carry these from the buggy
            // code path that retired Immutable forks; the Pass 1/Pass 2
            // predicate-classifier added in this PR keeps new saves clean, but
            // we still need to scrub legacy state on load.
            var committed = RecordingStore.CommittedRecordings;
            var immutableRecordingIds = new HashSet<string>(StringComparer.Ordinal);
            if (committed != null)
            {
                for (int i = 0; i < committed.Count; i++)
                {
                    var rec = committed[i];
                    if (rec == null || string.IsNullOrEmpty(rec.RecordingId)) continue;
                    if (rec.MergeState == MergeState.Immutable)
                        immutableRecordingIds.Add(rec.RecordingId);
                }
            }

            int removed = 0;
            int removedImmutableRetirements = 0;
            int removedLegacyNonImmutableOldSideRetirements = 0;
            int retainedWithMissingRestored = 0;
            for (int i = scenario.RecordingRewindRetirements.Count - 1; i >= 0; i--)
            {
                var retirement = scenario.RecordingRewindRetirements[i];
                if (retirement == null || string.IsNullOrEmpty(retirement.RecordingId))
                {
                    scenario.RecordingRewindRetirements.RemoveAt(i);
                    removed++;
                    continue;
                }

                bool retiredResolved = RecordingExists(retirement.RecordingId, knownRecordingIds);
                if (!retiredResolved)
                {
                    ParsekLog.Info(SupersedeTag,
                        $"Orphan rewind-retirement={retirement.RetirementId ?? "<no-id>"} " +
                        $"recording={retirement.RecordingId}; removing");
                    scenario.RecordingRewindRetirements.RemoveAt(i);
                    removed++;
                    continue;
                }

                // Pre-fix legacy old-side cleanup (this PR):
                // RewoundOutOldSideReason rows targeting a NON-Immutable
                // priorTip were authored by the deprecated Pass-2 code path
                // that retired priorTips for every dropped supersede regardless
                // of the fork's MergeState. The post-fix Pass 2 only writes
                // such rows when the dropped relation had an Immutable fork
                // (and isn't a forced self-rewind). In production demotion
                // shapes the priorTip is itself a fork retirement, so it never
                // reaches Pass 2's write site under the new rule — meaning any
                // live-priorTip non-Immutable RewoundOutOldSideReason row in
                // an existing save is pre-fix stale state. Sweep it so the
                // priorTip becomes visible again and Watch playback can replay
                // it. See docs/dev/plans/fix-tree-rewind-supersede-old-side.md.
                bool isOldSideOnNonImmutablePriorTip =
                    !immutableRecordingIds.Contains(retirement.RecordingId)
                    && string.Equals(retirement.Reason,
                        RecordingRewindRetirement.RewoundOutOldSideReason,
                        StringComparison.Ordinal);
                if (isOldSideOnNonImmutablePriorTip)
                {
                    ParsekLog.Info(SupersedeTag,
                        $"Removing legacy rewind-retirement={retirement.RetirementId ?? "<no-id>"} " +
                        $"recording={retirement.RecordingId} " +
                        $"reason=fork-non-immutable-priortip-pre-fix " +
                        "(pre-fix Pass-2 wrote this row before the new MergeState gate; " +
                        "priorTip will be visible after sweep so spawn-at-endpoint can replay it)");
                    scenario.RecordingRewindRetirements.RemoveAt(i);
                    removed++;
                    removedLegacyNonImmutableOldSideRetirements++;
                    continue;
                }

                // Defensive Immutable cleanup: a retirement pointing at a
                // canon Immutable recording is either:
                //   (a) Pre-fix legacy bad state. The old buggy code path
                //       retired Immutable forks unconditionally; that broke
                //       the canon contract. Cleanup: remove the retirement
                //       and reconstruct the dropped priorTip → canon
                //       supersede relation so the priorTip stays superseded.
                //   (b) Intentional Pass-2 demotion from this PR. The canon
                //       fork's priorTip was itself retired in the same
                //       rewind batch, so the canon collapses. The retirement
                //       carries Reason=DemotedCanonReason as a tag so this
                //       sweep can tell intent (b) apart from legacy (a).
                //
                // Without the reason tag a sweep would fail to disambiguate:
                // both `A → B(Imm) → C(Imm)` (legacy) and `A → B(Prov) →
                // C(Imm) → D(Imm)` (post-fix) produce retirements pointing
                // at Immutable forks whose RestoredRecordingId is also a
                // retired fork in the same batch.
                bool isImmutableTarget =
                    immutableRecordingIds.Contains(retirement.RecordingId);
                bool isIntentionalDemotedCanon = isImmutableTarget
                    && string.Equals(retirement.Reason,
                        RecordingRewindRetirement.DemotedCanonReason,
                        StringComparison.Ordinal);
                // Self-rewound canon retirements (this PR): user explicitly
                // clicked Rewind on the canon fork. The classifier forced
                // the drop and tagged the retirement with
                // SelfRewoundCanonReason. If the legacy-Immutable sweep
                // reconstructed the priorTip → canon relation here, the
                // user's self-rewind would be silently undone on every
                // load — the canon would become visible again and the
                // priorTip would re-hide. Skip these retirements.
                bool isIntentionalSelfRewoundCanon = isImmutableTarget
                    && string.Equals(retirement.Reason,
                        RecordingRewindRetirement.SelfRewoundCanonReason,
                        StringComparison.Ordinal);
                // Old-side retirements (PR #807) target priorTips of dropped
                // supersedes that were rewound out of existence. They are
                // intentional and authored with RestoredRecordingId=null, so
                // TryRestoreLegacyImmutableSupersede couldn't reconstruct
                // anything anyway. But guard explicitly so a future Immutable
                // priorTip (e.g. a canon Re-Fly stacked on top of another
                // canon) doesn't get its old-side retirement swept.
                bool isIntentionalOldSide = isImmutableTarget
                    && string.Equals(retirement.Reason,
                        RecordingRewindRetirement.RewoundOutOldSideReason,
                        StringComparison.Ordinal);
                if (isImmutableTarget
                    && !isIntentionalDemotedCanon
                    && !isIntentionalSelfRewoundCanon
                    && !isIntentionalOldSide)
                {
                    LegacyImmutableSupersedeRestoreResult restoreResult =
                        TryRestoreLegacyImmutableSupersede(
                            scenario, retirement, knownRecordingIds);
                    string restoreNote;
                    switch (restoreResult)
                    {
                        case LegacyImmutableSupersedeRestoreResult.Restored:
                            restoreNote = "and restored supersede relation priorTip→canon ";
                            break;
                        case LegacyImmutableSupersedeRestoreResult.AlreadyPresent:
                            restoreNote = "(supersede relation already present — left intact) ";
                            break;
                        case LegacyImmutableSupersedeRestoreResult.MissingMetadata:
                            restoreNote = "(no RestoredRecordingId in retirement metadata — supersede relation cannot be reconstructed; priorTip may render alongside canon, investigate) ";
                            break;
                        case LegacyImmutableSupersedeRestoreResult.RestoredRecordingMissing:
                            restoreNote = "(RestoredRecordingId no longer exists in committed store — supersede relation not reconstructed to avoid creating a one-sided orphan; priorTip may render alongside canon, investigate) ";
                            break;
                        default:
                            restoreNote = "(restore outcome=" + restoreResult.ToString() + ") ";
                            break;
                    }
                    ParsekLog.Warn(SupersedeTag,
                        $"Removing rewind-retirement={retirement.RetirementId ?? "<no-id>"} " +
                        $"pointing at Immutable canon recording={retirement.RecordingId} " +
                        restoreNote +
                        "(legacy pre-fix save state). The canon recording will be visible after sweep.");
                    scenario.RecordingRewindRetirements.RemoveAt(i);
                    removed++;
                    removedImmutableRetirements++;
                    continue;
                }

                bool restoredMissing = !string.IsNullOrEmpty(retirement.RestoredRecordingId)
                    && !RecordingExists(retirement.RestoredRecordingId, knownRecordingIds);
                if (restoredMissing)
                    retainedWithMissingRestored++;
            }

            int orphanOnly = removed
                - removedImmutableRetirements
                - removedLegacyNonImmutableOldSideRetirements;
            if (orphanOnly > 0)
            {
                ParsekLog.Info(SweepTag,
                    $"[LoadSweep] Cleaned {orphanOnly.ToString(CultureInfo.InvariantCulture)} " +
                    "orphan rewind-retirement row(s); persists on next OnSave");
            }
            if (removedImmutableRetirements > 0)
            {
                ParsekLog.Info(SweepTag,
                    $"[LoadSweep] Removed {removedImmutableRetirements.ToString(CultureInfo.InvariantCulture)} " +
                    "rewind-retirement row(s) pointing at Immutable canon recordings (legacy state cleanup)");
            }
            if (removedLegacyNonImmutableOldSideRetirements > 0)
            {
                ParsekLog.Info(SweepTag,
                    $"[LoadSweep] Removed {removedLegacyNonImmutableOldSideRetirements.ToString(CultureInfo.InvariantCulture)} " +
                    "rewind-retirement row(s) RewoundOutOldSide on non-Immutable priorTip(s) " +
                    "(pre-fix Pass-2 state cleanup; priorTip becomes visible again for spawn-at-endpoint)");
            }
            if (retainedWithMissingRestored > 0)
            {
                ParsekLog.Warn(SupersedeTag,
                    $"Retained {retainedWithMissingRestored.ToString(CultureInfo.InvariantCulture)} " +
                    "rewind-retirement row(s) whose restored recording no longer exists");
            }
            return removed;
        }

        private enum LegacyImmutableSupersedeRestoreResult
        {
            /// <summary>Relation reconstructed from retirement metadata.</summary>
            Restored,
            /// <summary>Equivalent priorTip → canon relation already in scenario; left intact.</summary>
            AlreadyPresent,
            /// <summary>Retirement is orphan or carries no RestoredRecordingId; cannot reconstruct.</summary>
            MissingMetadata,
            /// <summary>RestoredRecordingId names a recording that no longer exists in the live store.</summary>
            RestoredRecordingMissing,
        }

        /// <summary>
        /// Reconstruct the priorTip → canon supersede relation that the
        /// pre-fix buggy rewind code dropped alongside writing this
        /// retirement. Returns an outcome enum so callers can produce
        /// distinguishable log lines for each skip cause.
        /// </summary>
        private static LegacyImmutableSupersedeRestoreResult TryRestoreLegacyImmutableSupersede(
            ParsekScenario scenario,
            RecordingRewindRetirement retirement,
            HashSet<string> knownRecordingIds)
        {
            if (object.ReferenceEquals(null, scenario)
                || retirement == null
                || string.IsNullOrEmpty(retirement.RecordingId)
                || string.IsNullOrEmpty(retirement.RestoredRecordingId))
                return LegacyImmutableSupersedeRestoreResult.MissingMetadata;

            // Verify the priorTip recording still exists in the live store.
            // Without this check, a save where the priorTip was purged
            // out-of-band (e.g. by an earlier discard sweep, by manual user
            // delete, by a tree-discard purge) would have us synthesize a
            // one-sided orphan relation that survives until the next load —
            // SweepOrphanSupersedes ran earlier in this same sweep, so the
            // newly-injected orphan won't be cleaned this cycle.
            if (knownRecordingIds == null
                || !knownRecordingIds.Contains(retirement.RestoredRecordingId))
                return LegacyImmutableSupersedeRestoreResult.RestoredRecordingMissing;

            if (scenario.RecordingSupersedes == null)
                scenario.RecordingSupersedes = new List<RecordingSupersedeRelation>();

            // Idempotency: if any supersede already names the same priorTip →
            // canon pair, skip. The exact RelationId or UT does not matter
            // for visibility computation (EffectiveState reads only Old/New).
            for (int i = 0; i < scenario.RecordingSupersedes.Count; i++)
            {
                var existing = scenario.RecordingSupersedes[i];
                if (existing == null) continue;
                if (string.Equals(existing.OldRecordingId, retirement.RestoredRecordingId, StringComparison.Ordinal)
                    && string.Equals(existing.NewRecordingId, retirement.RecordingId, StringComparison.Ordinal))
                {
                    return LegacyImmutableSupersedeRestoreResult.AlreadyPresent;
                }
            }

            // Synthesize a fresh relation. Use the retirement's preserved
            // SourceSupersedeRelationId when available so audit trails
            // referencing the original RelationId still resolve. Fall back to
            // a "rsr_legacyrestore_<guid>" id otherwise. UT defaults to
            // retirement.CreatedUT (the moment the buggy retirement was
            // written) — the original merge UT is not recoverable.
            var restored = new RecordingSupersedeRelation
            {
                RelationId = !string.IsNullOrEmpty(retirement.SourceSupersedeRelationId)
                    ? retirement.SourceSupersedeRelationId
                    : "rsr_legacyrestore_" + Guid.NewGuid().ToString("N"),
                OldRecordingId = retirement.RestoredRecordingId,
                NewRecordingId = retirement.RecordingId,
                UT = retirement.CreatedUT,
            };
            scenario.RecordingSupersedes.Add(restored);
            return LegacyImmutableSupersedeRestoreResult.Restored;
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
                RecordingsTableUI.ClearRewindSlotCanInvokeLogState(rp.RewindPointId);
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

        private static bool RecordingExists(
            string recordingId,
            HashSet<string> knownRecordingIds)
        {
            if (string.IsNullOrEmpty(recordingId)) return false;
            return knownRecordingIds != null && knownRecordingIds.Contains(recordingId);
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
