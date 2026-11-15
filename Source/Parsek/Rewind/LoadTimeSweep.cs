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
            LedgerRowsRemovedByLastRun = 0;
            ZombiesDiscardedByLastRun = 0;
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
                scenario.ClearActiveReFlySessionMarker("marker-cleared");
                // The session-scoped prune hand-over dies with the session it
                // belonged to (see ReFlyProvisionalRetirement): reaching this
                // clear means the SWEEP ended the session rather than a
                // conclusion, so any leftover note must never be adopted by
                // whatever arms the next marker. Dropped by
                // ClearActiveReFlySessionMarker above - the pairing is central now.
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
            SweepValueSealedSlots(scenario);

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

                // The RP quicksave is gone, so no slot can ever be re-flown
                // again -> every slot is concluded. Close each slot by setting
                // its effective chain+supersede tip recording to Immutable
                // (open/closed is read from the tip MergeState, the single
                // source of truth after collapse-seal-into-mergestate). Tips are
                // disjoint across slots, so this cannot cross-close a sibling.
                if (rp.ChildSlots != null)
                {
                    IReadOnlyList<RecordingSupersedeRelation> supersedes =
                        scenario.RecordingSupersedes
                        ?? (IReadOnlyList<RecordingSupersedeRelation>)Array.Empty<RecordingSupersedeRelation>();
                    for (int s = 0; s < rp.ChildSlots.Count; s++)
                    {
                        var slot = rp.ChildSlots[s];
                        if (slot == null) continue;
                        string tipId = slot.EffectiveRecordingId(supersedes);
                        // Use the shared tree-aware resolver: a chain tip can
                        // live in a committed tree without being mirrored into
                        // the flat committed list, and a flat-only scan would
                        // miss it and leave the orphaned slot un-concluded.
                        Recording tip = EffectiveState.FindCommittedRecordingByIdRaw(tipId);
                        if (tip != null && tip.MergeState != MergeState.Immutable)
                        {
                            tip.MergeState = MergeState.Immutable;
                            tip.FilesDirty = true;
                            ParsekLog.Verbose(SweepTag,
                                $"Missing rewind-point quicksave concluded slot={s} " +
                                $"rp={rp.RewindPointId} tip={tipId} -> Immutable");
                        }
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

        internal struct ValueSealedSlotSweepResult
        {
            public int Found;
            public int Sealed;
            public int KeptLiveSession;
            public int RewindPointsReaped;
        }

        /// <summary>
        /// Migration for design section 2.12 Rule 1 (forward-only): concludes (flips
        /// Immutable) the open tip of every slot whose retire set holds a sealing row,
        /// stashed slots included, so the slot leaves Unfinished Flights and its rewind
        /// point can be reaped. A slot of the live Re-Fly session's rewind point is
        /// counted but left open (never seal a slot out from under a running session; a
        /// later load concludes it). Tombstones already written and deficits already in
        /// the save are not repaired. Idempotent: a concluded tip is Immutable, so a
        /// second pass finds nothing. Logs one summary line.
        /// </summary>
        internal static ValueSealedSlotSweepResult SweepValueSealedSlots(ParsekScenario scenario)
        {
            var result = new ValueSealedSlotSweepResult();
            if (object.ReferenceEquals(null, scenario) || scenario.RewindPoints == null)
            {
                LogValueSealedSlotSummary(result);
                return result;
            }

            string markerRewindPointId = scenario.ActiveReFlySessionMarker?.RewindPointId;
            IReadOnlyList<RecordingSupersedeRelation> supersedes =
                scenario.RecordingSupersedes
                ?? (IReadOnlyList<RecordingSupersedeRelation>)Array.Empty<RecordingSupersedeRelation>();
            var ic = CultureInfo.InvariantCulture;
            for (int i = 0; i < scenario.RewindPoints.Count; i++)
            {
                var rp = scenario.RewindPoints[i];
                if (rp?.ChildSlots == null) continue;
                for (int s = 0; s < rp.ChildSlots.Count; s++)
                {
                    var slot = rp.ChildSlots[s];
                    if (slot == null) continue;
                    string tipId = slot.EffectiveRecordingId(supersedes);
                    Recording tip = EffectiveState.FindCommittedRecordingByIdRaw(tipId);
                    if (tip == null || tip.MergeState != MergeState.CommittedProvisional)
                        continue;
                    string summary;
                    if (!SupersedeCommit.RetireSetHoldsSealingRow(tip, rp, slot, out summary))
                        continue;

                    result.Found++;
                    if (!string.IsNullOrEmpty(markerRewindPointId)
                        && string.Equals(rp.RewindPointId, markerRewindPointId, StringComparison.Ordinal))
                    {
                        result.KeptLiveSession++;
                        ParsekLog.Info(SweepTag,
                            $"Value-sealed slot kept open for the live Re-Fly session rp={rp.RewindPointId} " +
                            $"slot={s.ToString(ic)} tip={tipId} row=recordingAction:{summary} " +
                            $"sess={scenario.ActiveReFlySessionMarker?.SessionId ?? "<no-sess>"}");
                        continue;
                    }

                    tip.MergeState = MergeState.Immutable;
                    tip.FilesDirty = true;
                    result.Sealed++;
                    if (result.Sealed <= SupersedeCommit.PreRewindKeepLogCap)
                    {
                        ParsekLog.Verbose(SweepTag,
                            $"Value-sealed slot concluded rp={rp.RewindPointId} slot={s.ToString(ic)} " +
                            $"tip={tipId} row=recordingAction:{summary} -> Immutable (section 2.12 Rule 1)");
                    }
                }
            }

            // The reaper reads the tip MergeState directly; Run's step 10 bumps the
            // supersede / tombstone versions once for the whole sweep.
            if (result.Sealed > 0)
                result.RewindPointsReaped = RewindPointReaper.ReapOrphanedRPs();
            LogValueSealedSlotSummary(result);
            return result;
        }

        private static void LogValueSealedSlotSummary(ValueSealedSlotSweepResult result)
        {
            var ic = CultureInfo.InvariantCulture;
            ParsekLog.Info(SweepTag,
                $"[LoadSweep] Value-sealed slots (section 2.12): found={result.Found.ToString(ic)} " +
                $"sealed={result.Sealed.ToString(ic)} keptLiveSession={result.KeptLiveSession.ToString(ic)} " +
                $"rpsReaped={result.RewindPointsReaped.ToString(ic)}");
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

        // Nothing writes rewind-retirement rows any more; rows a save carries are read
        // as they are. Only rows naming no recording at all are removed here.
        private static int SweepOrphanRewindRetirements(ParsekScenario scenario)
        {
            if (object.ReferenceEquals(null, scenario)
                || scenario.RecordingRewindRetirements == null
                || scenario.RecordingRewindRetirements.Count == 0)
                return 0;

            var knownRecordingIds = RecordingStore.BuildKnownRecordingIdsForCleanup();

            int removed = 0;
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

                if (!RecordingExists(retirement.RecordingId, knownRecordingIds))
                {
                    ParsekLog.Info(SupersedeTag,
                        $"Orphan rewind-retirement={retirement.RetirementId ?? "<no-id>"} " +
                        $"recording={retirement.RecordingId}; removing");
                    scenario.RecordingRewindRetirements.RemoveAt(i);
                    removed++;
                    continue;
                }

                if (!string.IsNullOrEmpty(retirement.RestoredRecordingId)
                    && !RecordingExists(retirement.RestoredRecordingId, knownRecordingIds))
                    retainedWithMissingRestored++;
            }

            if (removed > 0)
            {
                ParsekLog.Info(SweepTag,
                    $"[LoadSweep] Cleaned {removed.ToString(CultureInfo.InvariantCulture)} " +
                    "orphan rewind-retirement row(s); persists on next OnSave");
            }
            if (retainedWithMissingRestored > 0)
            {
                ParsekLog.Warn(SupersedeTag,
                    $"Retained {retainedWithMissingRestored.ToString(CultureInfo.InvariantCulture)} " +
                    "rewind-retirement row(s) whose restored recording no longer exists");
            }
            return removed;
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

        /// <summary>
        /// Ledger rows the last <see cref="Run"/> removed with its zombie recordings (rewind
        /// design section 2.12 Rule 3). The in-session OnLoad branch runs the sweep after its
        /// ledger recalculation, so it recalculates again when this is non-zero; the cold branch
        /// recalculates later (the deferred seed coroutine).
        /// </summary>
        internal static int LedgerRowsRemovedByLastRun { get; private set; }

        /// <summary>
        /// Zombie Re-Fly attempts the last <see cref="Run"/> discarded. An attempt files its
        /// rows only at merge, so a zombie usually removes no ledger row, yet a save written
        /// during its session carries what it earned in the stock pools. Both OnLoad branches
        /// therefore walk with the Re-Fly discard flag when this is non-zero, so the patch takes
        /// the pools back to the ledger (todo REFLY-COLD-LOAD-ZOMBIE-KEEPS-ATTEMPT-POOLS).
        /// </summary>
        internal static int ZombiesDiscardedByLastRun { get; private set; }

        /// <summary>
        /// Pure: true when the walk after a sweep must take the pools back to the ledger
        /// (rewind design section 2.12 Rule 3, Discard restores <c>L</c>).
        /// </summary>
        internal static bool SweepDiscardedAnAttempt(int ledgerRowsRemoved, int zombiesDiscarded)
        {
            return ledgerRowsRemoved > 0 || zombiesDiscarded > 0;
        }

        private static int RemoveDiscardRecordings(List<Recording> discard)
        {
            LedgerRowsRemovedByLastRun = 0;
            ZombiesDiscardedByLastRun = 0;
            if (discard == null || discard.Count == 0) return 0;
            int removed = 0;
            int deletedFiles = 0;
            var discardedIds = new HashSet<string>(StringComparer.Ordinal);
            for (int i = 0; i < discard.Count; i++)
            {
                var rec = discard[i];
                if (rec == null) continue;

                // Bug fix-refly-abandon-and-fork-persist §Bug1 structural leak:
                // RemoveCommittedInternal only removes from the flat list. The
                // recording's node in its owning tree's Recordings dict survives
                // and FinalizeTreeCommit (RecordingStore.cs:1363-1386) re-adds
                // it to committedRecordings on the next commit pass. Walk every
                // tree dict here so the zombie cannot be resurrected. The
                // tree-dict walks are keyed by RecordingId, so a recording with
                // no id can only be removed from the flat list (which uses
                // reference equality via RemoveCommittedInternal).
                bool fromFlatList = RecordingStore.RemoveCommittedInternal(rec);
                int fromCommittedTrees = 0;
                bool fromPendingTree = false;
                bool fromActiveTree = false;
                if (!string.IsNullOrEmpty(rec.RecordingId))
                {
                    var committedTrees = RecordingStore.CommittedTrees;
                    if (committedTrees != null)
                    {
                        for (int t = 0; t < committedTrees.Count; t++)
                        {
                            var tree = committedTrees[t];
                            if (tree?.Recordings != null && tree.Recordings.Remove(rec.RecordingId))
                            {
                                tree.RebuildBackgroundMap();
                                fromCommittedTrees++;
                            }
                        }
                    }
                    var pending = PendingTreeSlot.PendingTree;
                    if (pending?.Recordings != null && pending.Recordings.Remove(rec.RecordingId))
                    {
                        pending.RebuildBackgroundMap();
                        fromPendingTree = true;
                    }
                    var active = ParsekFlight.Instance?.ActiveTreeForSerialization;
                    if (active?.Recordings != null && active.Recordings.Remove(rec.RecordingId))
                    {
                        active.RebuildBackgroundMap();
                        fromActiveTree = true;
                    }
                }

                bool ok = fromFlatList || fromCommittedTrees > 0 || fromPendingTree || fromActiveTree;
                if (!string.IsNullOrEmpty(rec.RecordingId))
                    discardedIds.Add(rec.RecordingId);
                if (ok)
                {
                    ParsekLog.Info(RewindTag,
                        $"Zombie discarded rec={rec.RecordingId ?? "<no-id>"} " +
                        $"sess={rec.CreatingSessionId ?? "<no-sess>"} " +
                        $"supersedeTarget={rec.SupersedeTargetId ?? "<none>"} " +
                        $"removedFromFlatList={fromFlatList} " +
                        $"removedFromCommittedTrees={fromCommittedTrees.ToString(CultureInfo.InvariantCulture)} " +
                        $"removedFromPendingTree={fromPendingTree} " +
                        $"removedFromActiveTree={fromActiveTree}");
                    removed++;
                    // A zombie never committed, so nothing else reads its sidecars; a Retry
                    // otherwise leaves the first attempt's files in the save.
                    if (RecordingFileJanitor.DeleteRecordingFiles(rec))
                        deletedFiles++;
                }
                else
                {
                    ParsekLog.Verbose(RewindTag,
                        $"Zombie recording not found in any collection rec={rec.RecordingId ?? "<no-id>"}");
                }
            }
            // A zombie is an abandoned, never-committed Re-Fly attempt: its ledger rows go with
            // it, like every other Re-Fly discard (rewind design section 2.12 Rule 3).
            LedgerRowsRemovedByLastRun = Ledger.RemoveActionsForRecordings(discardedIds, "LoadTimeSweep:zombie");
            ZombiesDiscardedByLastRun = removed;
            ParsekLog.Info(RewindTag,
                $"Zombies discarded={removed.ToString(CultureInfo.InvariantCulture)} " +
                $"ledgerRowsRemoved={LedgerRowsRemovedByLastRun.ToString(CultureInfo.InvariantCulture)} " +
                $"deletedFiles={deletedFiles.ToString(CultureInfo.InvariantCulture)}");
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

                RewindPointReaper.ReleaseQuicksaveFile(rp);
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
