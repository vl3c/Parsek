using System;
using System.Collections.Generic;
using System.Globalization;

namespace Parsek
{
    /// <summary>
    /// Phase 10 of Rewind-to-Staging (design §5.8, §6.6, §6.9 step 2, §10.8):
    /// journaled staged-commit orchestrator for merging a re-fly session. Wraps
    /// the in-memory <see cref="SupersedeCommit"/> steps in five crash-recovery
    /// checkpoints, persists the stable merge-path barriers to disk, and
    /// exposes the load-time finisher that resumes an interrupted merge.
    ///
    /// <para>
    /// The 14 granular design-doc steps are consolidated into five recovery
    /// windows (per plan Phase 10 §6.6 matrix):
    /// </para>
    /// <list type="bullet">
    ///   <item><description><b>Supersede append</b> — relations half-written.</description></item>
    ///   <item><description><b>Tombstone scan</b> — tombstones half-written.</description></item>
    ///   <item><description><b>Reservation recompute</b> — tombstones done but MergeState not flipped.</description></item>
    ///   <item><description><b>Durable save</b> — memory committed, disk not yet written.</description></item>
    ///   <item><description><b>Reap check</b> — Durable1 done, RPs not yet reaped.</description></item>
    /// </list>
    ///
    /// <para>
    /// The finisher on load recovers from ANY of these windows. Phase less-than-
    /// or-equal <see cref="MergeJournal.Phases.Finalize"/> rolls back to the
    /// pre-merge state (the Durable1 write never happened, so disk still holds
    /// the pre-merge snapshot). Phase greater-than-or-equal
    /// <see cref="MergeJournal.Phases.Durable1Done"/> completes the remaining
    /// steps (reap, marker clear, journal clear, durable saves 2 and 3).
    /// </para>
    ///
    /// <para>
    /// Side-effects:
    /// <see cref="RecordingSupersedeRelation"/> append via
    /// <see cref="SupersedeCommit.AppendRelations"/>;
    /// <see cref="LedgerTombstone"/> append via
    /// <see cref="SupersedeCommit.CommitTombstones"/>;
    /// <see cref="Recording.MergeState"/> flip via
    /// <see cref="SupersedeCommit.FlipMergeStateAndClearTransient"/>;
    /// <see cref="CrewReservationManager.RecomputeAfterTombstones"/>;
    /// <see cref="ParsekScenario.ActiveReFlySessionMarker"/> clear;
    /// <see cref="ParsekScenario.ActiveMergeJournal"/> write + clear;
    /// scenario state-version bumps via
    /// <see cref="ParsekScenario.BumpSupersedeStateVersion"/> +
    /// <see cref="ParsekScenario.BumpTombstoneStateVersion"/>;
    /// four persistent saves at the stable barriers (pluggable via
    /// <see cref="DurableSaveForTesting"/> / <see cref="SaveGameForTesting"/>):
    /// Begin, Durable1Done, Durable2Done, and the final cleared state.
    /// </para>
    ///
    /// <para>
    /// Phase 11 extends the RpReap checkpoint: after the session-provisional
    /// flag is flipped (<see cref="TagRpsForReap"/>), the reaper
    /// (<see cref="RewindPointReaper.ReapOrphanedRPs"/>) deletes the quicksave
    /// file + scenario entry + BranchPoint back-reference for any RP whose
    /// slots all resolve to <see cref="MergeState.Immutable"/>. RPs with an
    /// open slot (NotCommitted / CommittedProvisional) stay put for a later
    /// pass.
    /// </para>
    /// </summary>
    internal static class MergeJournalOrchestrator
    {
        private const string Tag = "MergeJournal";

        /// <summary>
        /// Phase enum mirroring <see cref="MergeJournal.Phases"/> string
        /// constants. Used for orchestrator-internal decisions + the test-only
        /// <see cref="FaultInjectionPoint"/> hook; the on-disk string is what
        /// ultimately drives the finisher.
        /// </summary>
        internal enum Phase
        {
            None,
            Begin,
            Supersede,
            Tombstone,
            Finalize,
            Durable1Done,
            RpReap,
            MarkerCleared,
            Durable2Done,
            Complete,
        }

        /// <summary>
        /// Test seam: when non-null, <see cref="RunMerge"/> throws
        /// <see cref="FaultInjectionException"/> immediately after advancing
        /// the journal to the matching phase. Unit tests use this to simulate
        /// the five crash-recovery windows defined in §6.6 and then call
        /// <see cref="RunFinisher"/> to exercise the recovery path.
        /// </summary>
        internal static Phase? FaultInjectionPoint;

        /// <summary>
    /// Test seam: when non-null, replaces the durable-checkpoint behavior with
    /// the injected callback.
    ///
    /// <para>
    /// Production <see cref="RunMerge"/> persists the stable barriers
    /// (<c>Begin</c>, <c>Durable1Done</c>, <c>Durable2Done</c>, and
    /// <c>Complete</c>) by synchronously saving <c>persistent.sfs</c>.
    /// <see cref="RunFinisher"/> intentionally does NOT do that because it
    /// executes during <see cref="ParsekScenario.OnLoad"/> and must not
    /// re-enter <c>OnSave</c>.
    /// </para>
    /// </summary>
        internal static Action<string> DurableSaveForTesting;

        /// <summary>
        /// Test seam for the production persistent-save call used by
        /// <see cref="RunMerge"/>'s stable durability barriers.
        /// </summary>
        internal static Func<string, string, SaveMode, string> SaveGameForTesting;

        /// <summary>
        /// Clears all test seams. Called from the <c>Dispose</c> of any test
        /// class that touched <see cref="FaultInjectionPoint"/> or
        /// <see cref="DurableSaveForTesting"/>.
        /// </summary>
        internal static void ResetTestOverrides()
        {
            FaultInjectionPoint = null;
            DurableSaveForTesting = null;
            SaveGameForTesting = null;
        }

        /// <summary>
        /// Exception thrown by the fault-injection hook. Only surfaces in
        /// tests; production code never raises this type.
        /// </summary>
        internal sealed class FaultInjectionException : Exception
        {
            public Phase InjectedPhase { get; }
            public FaultInjectionException(Phase phase)
                : base($"MergeJournalOrchestrator fault injected at phase={phase}")
            {
                InjectedPhase = phase;
            }
        }

        /// <summary>
        /// Drives the staged-commit merge sequence (design §6.6 steps 1-14).
        /// Returns <c>true</c> on success, <c>false</c> on early no-op (null
        /// inputs or no live scenario). Exceptions propagate so the caller
        /// (currently <see cref="MergeDialog.MergeCommit"/>) can log-and-toast;
        /// the persisted <see cref="MergeJournal"/> ensures the next load
        /// resumes the merge from the last checkpoint.
        /// </summary>
        internal static bool RunMerge(ReFlySessionMarker marker, Recording provisional)
        {
            if (marker == null)
            {
                ParsekLog.Warn(Tag, "RunMerge: marker is null — nothing to commit");
                return false;
            }
            if (provisional == null)
            {
                ParsekLog.Warn(Tag, "RunMerge: provisional is null — nothing to commit");
                return false;
            }

            var scenario = ParsekScenario.Instance;
            if (ReferenceEquals(null, scenario))
            {
                ParsekLog.Warn(Tag, "RunMerge: no ParsekScenario instance — nothing to commit");
                return false;
            }

            string sessionId = marker.SessionId ?? "<no-id>";
            string provisionalId = provisional.RecordingId ?? "<no-id>";
            double startedUT = SafeNow();
            string startedRealTime = DateTime.UtcNow.ToString("o");

            SupersedeCommit.PreflightMergeStateClassification(marker, provisional, scenario);

            if (scenario.RecordingSupersedes == null)
                scenario.RecordingSupersedes = new List<RecordingSupersedeRelation>();
            if (scenario.LedgerTombstones == null)
                scenario.LedgerTombstones = new List<LedgerTombstone>();

            // Step 1: write the journal + Durable Save #1 barrier (phase=Begin).
            // Design §6.6 step 1 / §10.8.
            var journal = new MergeJournal
            {
                JournalId = "mj_" + Guid.NewGuid().ToString("N"),
                SessionId = marker.SessionId,
                TreeId = marker.TreeId,
                Phase = MergeJournal.Phases.Begin,
                StartedUT = startedUT,
                StartedRealTime = startedRealTime,
            };
            scenario.ActiveMergeJournal = journal;
            ParsekLog.Info(Tag,
                $"sess={sessionId} phase={MergeJournal.Phases.Begin}");
            DurableSave("begin", persistSynchronously: true);
            MaybeInject(Phase.Begin);

            // Step 2: supersede relations (§6.6 step 3).
            var subtree = SupersedeCommit.AppendRelations(marker, provisional, scenario);
            AdvancePhase(scenario, MergeJournal.Phases.Supersede);
            MaybeInject(Phase.Supersede);

            // Step 3: tombstone scan (§6.6 step 4).
            string nowIso = startedRealTime;
            SupersedeCommit.CommitTombstones(marker, subtree, provisionalId, startedUT, nowIso, scenario);
            AdvancePhase(scenario, MergeJournal.Phases.Tombstone);
            MaybeInject(Phase.Tombstone);

            // Step 4: flip MergeState + clear SupersedeTargetId + bump supersede
            // version so ERS rebuilds; clear active marker is deferred until
            // step 7 so the design §6.6 sequencing matches (marker clears AFTER
            // Durable Save #1).
            SupersedeCommit.FlipMergeStateAndClearTransient(marker, provisional, scenario, preserveMarker: true);
            AdvancePhase(scenario, MergeJournal.Phases.Finalize);
            MaybeInject(Phase.Finalize);

            AdvancePhase(scenario, MergeJournal.Phases.Durable1Done);
            // Step 5: Durable Save #1 (§6.6 step 8). Memory and disk agree on
            // supersedes + tombstones + MergeState + reservation state. Marker
            // + RPs still present.
            DurableSave("durable1", persistSynchronously: true);
            MaybeInject(Phase.Durable1Done);

            // Step 6: tag session-provisional RPs for reap (§6.6 step 9) and
            // run the Phase 11 reaper so any RPs whose slots are all now
            // Immutable have their quicksave file + scenario entry + BP back-
            // reference removed. The tag flips SessionProvisional=false, which
            // is a precondition for reap eligibility; the reaper then scans
            // for RPs whose ChildSlots resolve to Immutable recordings. An RP
            // whose slots are still CommittedProvisional / NotCommitted stays
            // put for a later reap pass (load-time sweep or the next merge).
            TagRpsForReap(marker, scenario);
            RewindPointReaper.ReapOrphanedRPs();
            AdvancePhase(scenario, MergeJournal.Phases.RpReap);
            MaybeInject(Phase.RpReap);

            // Step 7: clear marker (§6.6 step 11).
            scenario.ActiveReFlySessionMarker = null;
            Parsek.Rendering.RenderSessionState.Clear("marker-cleared");
            scenario.BumpSupersedeStateVersion();
            ReFlyRevertButtonGate.Apply("MergeJournal:marker-cleared");
            ParsekLog.Info("ReFlySession",
                $"End reason=merged sess={sessionId} provisional={provisionalId}");
            AdvancePhase(scenario, MergeJournal.Phases.MarkerCleared);
            MaybeInject(Phase.MarkerCleared);

            AdvancePhase(scenario, MergeJournal.Phases.Durable2Done);
            // Step 8: Durable Save #2 (§6.6 step 12).
            DurableSave("durable2", persistSynchronously: true);
            MaybeInject(Phase.Durable2Done);

            // Step 9: clear journal + Durable Save #3 (§6.6 steps 13-14).
            ClearJournalAndFinalSave(scenario, sessionId, persistSynchronously: true);
            return true;
        }

        /// <summary>
        /// Resumes a crashed-mid-merge staged commit (design §6.9 step 2).
        /// Called from <see cref="ParsekScenario.DispatchRewindPostLoadIfPending"/>'s
        /// surrounding OnLoad block when a journal is present on disk.
        ///
        /// <para>
        /// Returns <c>false</c> if no journal is present (no work). Returns
        /// <c>true</c> when either the rollback path or the completion path
        /// drove the journal to cleared. Idempotent: re-entry after completion
        /// observes no journal and returns false.
        /// </para>
        /// </summary>
        internal static bool RunFinisher()
        {
            var scenario = ParsekScenario.Instance;
            if (ReferenceEquals(null, scenario))
            {
                ParsekLog.Verbose(Tag, "RunFinisher: no ParsekScenario instance — nothing to finish");
                return false;
            }

            var journal = scenario.ActiveMergeJournal;
            if (journal == null)
            {
                ParsekLog.Verbose(Tag, "RunFinisher: no active journal — nothing to finish");
                return false;
            }

            string sessionId = journal.SessionId ?? "<no-id>";
            string phase = journal.Phase ?? MergeJournal.Phases.Begin;
            bool markerPresent = scenario.ActiveReFlySessionMarker != null;
            ParsekLog.Info(Tag,
                $"Finisher sess={sessionId} markerWas={(markerPresent ? "present" : "absent")} phase={phase}");

            if (phase == MergeJournal.Phases.Complete)
            {
                // Legacy/older saves may persist Phase=Complete instead of the
                // fully-cleared terminal state. Clear idempotently.
                ClearJournalAndFinalSave(scenario, sessionId, persistSynchronously: false);
                return true;
            }

            if (MergeJournal.IsPreDurablePhase(phase))
            {
                RollBack(scenario, journal, sessionId, phase);
                return true;
            }

            if (MergeJournal.IsPostDurablePhase(phase))
            {
                CompleteFromPostDurable(scenario, journal, sessionId, phase);
                return true;
            }

            // Unknown phase string: treat as pre-durable roll-back (safer than
            // blowing up the finisher; design §6.9 step 2 mandates idempotence).
            ParsekLog.Warn(Tag,
                $"Finisher: unknown phase={phase} sess={sessionId} — treating as rollback");
            RollBack(scenario, journal, sessionId, phase);
            return true;
        }

        // ------------------------------------------------------------------
        // Rollback path (crash at or before Finalize).
        // ------------------------------------------------------------------

        private static void RollBack(
            ParsekScenario scenario, MergeJournal journal, string sessionId, string fromPhase)
        {
            // The in-memory supersede/tombstone/flip never reached Durable
            // Save #1, so disk still holds the pre-merge snapshot. Any residual
            // in-memory state (loaded by OnLoad from a save pre-crash) matches
            // disk — removal of a session-provisional recording is idempotent
            // when the recording is already absent.
            int removedProvisional = 0;
            string provisionalId =
                scenario.ActiveReFlySessionMarker?.ActiveReFlyRecordingId;
            if (!string.IsNullOrEmpty(provisionalId))
            {
                removedProvisional = RemoveCommittedRecordingById(provisionalId);
            }

            bool hadMarker = scenario.ActiveReFlySessionMarker != null;
            scenario.ActiveReFlySessionMarker = null;
            Parsek.Rendering.RenderSessionState.Clear("marker-cleared");
            scenario.ActiveMergeJournal = null;
            scenario.BumpSupersedeStateVersion();
            ReFlyRevertButtonGate.Apply("MergeJournal:rollback");

            DurableSave("rollback", persistSynchronously: false);

            ParsekLog.Info(Tag,
                $"Rolled back from phase={fromPhase}: session restored sess={sessionId} " +
                $"markerCleared={hadMarker} provisionalRemoved={removedProvisional}");
            ParsekLog.Verbose(Tag, $"sess={sessionId} cleared");
        }

        // ------------------------------------------------------------------
        // Completion path (crash after Durable1).
        // ------------------------------------------------------------------

        private static void CompleteFromPostDurable(
            ParsekScenario scenario, MergeJournal journal, string sessionId, string fromPhase)
        {
            int stepsDriven = 0;

            if (fromPhase == MergeJournal.Phases.Durable1Done)
            {
                TagRpsForReap(scenario.ActiveReFlySessionMarker, scenario);
                RewindPointReaper.ReapOrphanedRPs();
                AdvancePhase(scenario, MergeJournal.Phases.RpReap);
                stepsDriven++;
            }

            if (journal.Phase == MergeJournal.Phases.RpReap)
            {
                if (scenario.ActiveReFlySessionMarker != null)
                {
                    string provisionalId =
                        scenario.ActiveReFlySessionMarker.ActiveReFlyRecordingId ?? "<no-id>";
                    ParsekLog.Info("ReFlySession",
                        $"End reason=merged sess={sessionId} provisional={provisionalId}");
                }
                scenario.ActiveReFlySessionMarker = null;
                Parsek.Rendering.RenderSessionState.Clear("marker-cleared");
                scenario.BumpSupersedeStateVersion();
                ReFlyRevertButtonGate.Apply("MergeJournal:complete-marker-cleared");
                AdvancePhase(scenario, MergeJournal.Phases.MarkerCleared);
                stepsDriven++;
            }

            if (journal.Phase == MergeJournal.Phases.MarkerCleared)
            {
                AdvancePhase(scenario, MergeJournal.Phases.Durable2Done);
                DurableSave("finisher-durable2", persistSynchronously: false);
                stepsDriven++;
            }

            ClearJournalAndFinalSave(scenario, sessionId, persistSynchronously: false);
            ParsekLog.Info(Tag,
                $"Completed from phase={fromPhase} sess={sessionId} stepsDriven={stepsDriven.ToString(CultureInfo.InvariantCulture)}");
        }

        // ------------------------------------------------------------------
        // Helpers.
        // ------------------------------------------------------------------

        private static void AdvancePhase(ParsekScenario scenario, string phase)
        {
            // ReferenceEquals: bypass Unity's Object == null override so a
            // test fixture scenario without a Unity lifecycle still validates.
            if (ReferenceEquals(null, scenario) || scenario.ActiveMergeJournal == null)
            {
                ParsekLog.Warn(Tag, $"AdvancePhase: no active journal at phase={phase}");
                return;
            }
            scenario.ActiveMergeJournal.Phase = phase;
            ParsekLog.Verbose(Tag,
                $"sess={scenario.ActiveMergeJournal.SessionId ?? "<no-id>"} phase={phase}");
        }

        private static void DurableSave(string label, bool persistSynchronously)
        {
            var hook = DurableSaveForTesting;
            if (hook != null)
            {
                hook(label);
                return;
            }

            if (!persistSynchronously)
            {
                ParsekLog.Verbose(Tag,
                    $"DurableSave checkpoint={label} (deferred: load-time finisher avoids SaveGame re-entry)");
                return;
            }

            var saveFn = SaveGameForTesting ?? GamePersistence.SaveGame;
            if (SaveGameForTesting == null)
            {
                if (HighLogic.CurrentGame == null)
                    throw new InvalidOperationException(
                        $"DurableSave checkpoint={label} failed: HighLogic.CurrentGame is null");
                if (string.IsNullOrEmpty(HighLogic.SaveFolder))
                    throw new InvalidOperationException(
                        $"DurableSave checkpoint={label} failed: HighLogic.SaveFolder is empty");
            }

            string result = saveFn("persistent", HighLogic.SaveFolder, SaveMode.OVERWRITE);
            if (string.IsNullOrEmpty(result))
            {
                throw new InvalidOperationException(
                    $"DurableSave checkpoint={label} failed: GamePersistence.SaveGame returned null");
            }

            ParsekLog.Info(Tag,
                $"DurableSave checkpoint={label} persisted via persistent.sfs");
        }

        private static void ClearJournalAndFinalSave(
            ParsekScenario scenario, string sessionId, bool persistSynchronously)
        {
            // §6.6 step 13-14 + §10.8 "Cleared" log.
            //
            // Production RunMerge must durably persist the *cleared* journal
            // state, so clear before the final SaveGame call. The load-time
            // finisher cannot do that because it must not re-enter OnSave;
            // there we keep the legacy Complete marker semantics and clear
            // only in memory.
            if (persistSynchronously)
            {
                scenario.ActiveMergeJournal = null;
                DurableSave("durable3", persistSynchronously: true);
            }
            else
            {
                if (scenario.ActiveMergeJournal != null)
                    scenario.ActiveMergeJournal.Phase = MergeJournal.Phases.Complete;
                DurableSave("durable3", persistSynchronously: false);
                scenario.ActiveMergeJournal = null;
            }
            ParsekLog.Verbose(Tag, $"sess={sessionId} cleared");
            ParsekLog.Info(Tag, $"Completed sess={sessionId}");
        }

        /// <summary>
        /// Phase 10 scope: tag the session-provisional RPs for reap (design
        /// §6.6 step 9). Phase 11 performs the file deletion; Phase 10 only
        /// clears the <see cref="RewindPoint.SessionProvisional"/> flag that
        /// keeps them restricted to the current session so the load-time sweep
        /// sees them as persistent-but-reap-eligible. Also promotes the marker's
        /// origin RP when it came from a normal staging split with no creating
        /// session id (old saves or pre-merge-dialog promotion windows).
        /// </summary>
        internal static void TagRpsForReap(ReFlySessionMarker marker, ParsekScenario scenario)
        {
            if (ReferenceEquals(null, scenario) || scenario.RewindPoints == null) return;
            string sessionId = marker?.SessionId;
            string markerRpId = marker?.RewindPointId;
            if (string.IsNullOrEmpty(sessionId))
            {
                ParsekLog.Verbose(Tag, "TagRpsForReap: no session id — skipping RP tag pass");
                return;
            }

            int promotedFromSession = 0;
            int promotedFromNormalOrigin = 0;
            for (int i = 0; i < scenario.RewindPoints.Count; i++)
            {
                var rp = scenario.RewindPoints[i];
                if (rp == null) continue;
                if (!rp.SessionProvisional) continue;
                bool sameSession = string.Equals(rp.CreatingSessionId, sessionId, StringComparison.Ordinal);
                bool normalOrigin = string.IsNullOrEmpty(rp.CreatingSessionId)
                    && !string.IsNullOrEmpty(markerRpId)
                    && string.Equals(rp.RewindPointId, markerRpId, StringComparison.Ordinal);
                if (!sameSession && !normalOrigin)
                    continue;
                rp.SessionProvisional = false;
                rp.CreatingSessionId = null;
                if (sameSession) promotedFromSession++;
                else promotedFromNormalOrigin++;
            }
            int taggedTotal = promotedFromSession + promotedFromNormalOrigin;
            ParsekLog.Info(Tag,
                $"TagRpsForReap: promoted {taggedTotal.ToString(CultureInfo.InvariantCulture)} session-provisional RP(s) for sess={sessionId} " +
                $"(fromSession={promotedFromSession.ToString(CultureInfo.InvariantCulture)}, " +
                $"fromNormalOrigin={promotedFromNormalOrigin.ToString(CultureInfo.InvariantCulture)})");
        }

        private static int RemoveCommittedRecordingById(string recordingId)
        {
            if (string.IsNullOrEmpty(recordingId)) return 0;
            var committed = RecordingStore.CommittedRecordings;
            if (committed == null) return 0;
            for (int i = 0; i < committed.Count; i++)
            {
                var rec = committed[i];
                if (rec == null) continue;
                if (!string.Equals(rec.RecordingId, recordingId, StringComparison.Ordinal)) continue;
                RecordingStore.DeleteRecordingFull(i);
                ParsekLog.Info(Tag,
                    $"Rollback: removed session-provisional recording id={recordingId}");
                return 1;
            }
            return 0;
        }

        private static void MaybeInject(Phase current)
        {
            if (FaultInjectionPoint.HasValue && FaultInjectionPoint.Value == current)
            {
                ParsekLog.Warn(Tag, $"Fault injected at phase={current}");
                throw new FaultInjectionException(current);
            }
        }

        private static double SafeNow()
        {
            try { return Planetarium.GetUniversalTime(); }
            catch { return 0.0; }
        }
    }
}
