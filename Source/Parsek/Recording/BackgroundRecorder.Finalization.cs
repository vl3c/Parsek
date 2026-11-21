using System;
using System.Collections.Generic;
using System.Globalization;
using KSP.UI.Screens;
using UnityEngine;

namespace Parsek
{
    // Recording finalization: persisting finalized recordings, commit finalization and the per-vessel finalization caches.
    internal partial class BackgroundRecorder
    {
        // Vessel pid -> recording id whose terminal the "background_destroy" refresh stamped
        // Destroyed eagerly (a loaded crash). Read by the false-destroy branch of
        // ParsekFlight.DeferredDestructionCheck, which must take that stamp back when the
        // vessel turns out to be alive (only unloaded).
        private readonly Dictionary<uint, string> eagerDestroyStampedRecordings =
            new Dictionary<uint, string>();

        internal void ForgetFinalizationCache(uint vesselPid)
        {
            finalizationCaches.Remove(vesselPid);
            eagerDestroyStampedRecordings.Remove(vesselPid);
        }

        /// <summary>
        /// Notes that the destroy refresh just stamped <paramref name="recording"/> Destroyed
        /// (its terminal was unset before the refresh and is Destroyed after it).
        /// </summary>
        private void NoteEagerDestroyStamp(
            uint vesselPid,
            Recording recording,
            bool hadTerminalBeforeRefresh)
        {
            if (recording == null
                || !ShouldTrackEagerDestroyStamp(hadTerminalBeforeRefresh, recording.TerminalStateValue))
            {
                return;
            }

            eagerDestroyStampedRecordings[vesselPid] = recording.RecordingId;
            ParsekLog.Verbose("BgRecorder",
                $"Destroy refresh stamped Destroyed eagerly: pid={vesselPid} rec={recording.RecordingId} " +
                "(taken back if the destruction turns out false)");
        }

        internal static bool ShouldTrackEagerDestroyStamp(
            bool hadTerminalBeforeRefresh,
            TerminalState? terminalAfterRefresh)
        {
            return !hadTerminalBeforeRefresh && terminalAfterRefresh == TerminalState.Destroyed;
        }

        /// <summary>
        /// Pure decision for the false-destroy branch: the Destroyed stamp is taken back only
        /// when the destroy refresh itself wrote it (a stamp from any other source stays) and
        /// the recording still carries it.
        /// </summary>
        internal static bool ShouldClearEagerDestroyStampAfterFalseDestroy(
            bool stampedByDestroyRefresh,
            string stampedRecordingId,
            string recordingId,
            TerminalState? terminal)
        {
            return stampedByDestroyRefresh
                && !string.IsNullOrEmpty(recordingId)
                && string.Equals(stampedRecordingId, recordingId, StringComparison.Ordinal)
                && terminal == TerminalState.Destroyed;
        }

        /// <summary>
        /// The false-destroy branch of <c>ParsekFlight.DeferredDestructionCheck</c>: the vessel
        /// still exists (it was only unloaded), so a Destroyed terminal the destroy refresh
        /// stamped eagerly is stale. Clears it, drops the destroy refresh's cache (the next
        /// refresh re-reads the live vessel) and logs. Returns true when a stamp was cleared.
        /// </summary>
        internal bool ClearEagerDestroyStampAfterFalseDestroy(uint vesselPid, string recordingId)
        {
            string stampedRecordingId;
            bool stamped = eagerDestroyStampedRecordings.TryGetValue(vesselPid, out stampedRecordingId);
            eagerDestroyStampedRecordings.Remove(vesselPid);

            Recording rec = null;
            if (tree != null && tree.Recordings != null && !string.IsNullOrEmpty(recordingId))
                tree.Recordings.TryGetValue(recordingId, out rec);

            if (!ShouldClearEagerDestroyStampAfterFalseDestroy(
                    stamped, stampedRecordingId, recordingId, rec?.TerminalStateValue))
            {
                ParsekLog.Verbose("BgRecorder",
                    $"False destroy: no eager Destroyed stamp to clear pid={vesselPid} " +
                    $"rec={recordingId ?? "(null)"} stampedByDestroyRefresh={stamped} " +
                    $"terminal={rec?.TerminalStateValue?.ToString() ?? "(null)"}");
                return false;
            }

            rec.StampTerminalState(null, "BackgroundRecorder.FalseDestroyClearEagerStamp");
            rec.MarkFilesDirty();

            RecordingFinalizationCache cache;
            bool droppedCache = finalizationCaches.TryGetValue(vesselPid, out cache)
                && cache != null
                && string.Equals(
                    cache.RefreshReason,
                    RecordingFinalizationCacheProducer.BackgroundDestroyRefreshReason,
                    StringComparison.Ordinal);
            if (droppedCache)
                finalizationCaches.Remove(vesselPid);

            ParsekLog.Info("BgRecorder",
                $"False destroy: cleared the eager Destroyed stamp pid={vesselPid} rec={recordingId} " +
                $"droppedDestroyRefreshCache={droppedCache}; the vessel is alive and keeps recording");
            return true;
        }

        internal void NoteEagerDestroyStampForTesting(uint vesselPid, Recording recording, bool hadTerminalBeforeRefresh)
        {
            NoteEagerDestroyStamp(vesselPid, recording, hadTerminalBeforeRefresh);
        }

        /// <summary>
        /// Writes a background recording's .prec sidecar to disk immediately after a
        /// finalization flush, bypassing OnSave's FilesDirty-driven path. Used by
        /// destroy/shutdown/split sites where the in-memory data must survive any
        /// scene reload that might happen before the next OnSave.
        ///
        /// Background: the 2026-04-09 playtest revealed ~16 debris recordings losing
        /// all their trajectory data on F9 quickload. BackgroundRecorder flushed the
        /// TrackSections to the Recording in memory and called <c>MarkFilesDirty</c>,
        /// but by OnSave time the files were never written — so on reload, the
        /// missing files produced 0-point recordings, and the subsequent CommitTree
        /// pass wrote those empty recordings over the in-memory data. Persisting at
        /// flush time closes the window between "data exists in memory" and "data
        /// exists on disk" (bug #280).
        /// DeferredDestructionCheck calls this directly for the same invariant:
        /// terminal sidecars must match in-memory state before later scene or merge
        /// transitions.
        /// </summary>
        internal static void PersistFinalizedRecording(Recording rec, string context)
        {
            if (rec == null) return;
            DebrisRelativeRecorderPolicy.NormalizeParentAnchoredRelativeRecording(
                rec,
                $"PersistFinalizedRecording {context}");
            if (RecordingSidecarStore.SaveRecordingFiles(rec, incrementEpoch: false))
            {
                ParsekLog.Verbose("BgRecorder",
                    $"PersistFinalizedRecording: wrote sidecar for " +
                    $"recId={rec.RecordingId} ({context})");
            }
            else
            {
                ParsekLog.Warn("BgRecorder",
                    $"PersistFinalizedRecording: failed to write sidecar for " +
                    $"recId={rec.RecordingId} ({context})");
            }
        }

        /// <summary>
        /// Finalizes all background recordings for tree commit.
        /// Closes all open orbit segments, flushes any pending trajectory data,
        /// and sets ExplicitEndUT on all background recordings.
        /// Does NOT clear state — call Shutdown() after this if shutting down.
        /// </summary>
        public void FinalizeAllForCommit(double commitUT)
        {
            // Never drop one (owner ruling 2026-11-18): open separation windows become their
            // branch points before the recordings are finalized.
            FlushBackgroundSeparationWindows("FinalizeAllForCommit");

            // Close all open orbit segments (skip destroyed recordings — their
            // recording is sealed, no open segments should exist, and any cache
            // refresh here would overwrite the terminal verdict).
            foreach (var kvp in onRailsStates)
            {
                var state = kvp.Value;
                if (!state.hasOpenOrbitSegment) continue;

                Recording finalizeOrbitTreeRec;
                if (tree.Recordings.TryGetValue(state.recordingId, out finalizeOrbitTreeRec)
                    && finalizeOrbitTreeRec.VesselDestroyed)
                {
                    continue;
                }

                RefreshOnRailsFinalizationCache(
                    state,
                    commitUT,
                    "background_commit_on_rails",
                    force: true);
                CloseOrbitSegment(state, commitUT);
            }

            // Close and flush TrackSections for all loaded vessels, emit terminal engine/RCS/robotic events (bug #108).
            // Skip destroyed recordings — flushing a new TrackSection at commitUT
            // would extend Recording.EndUT (which uses playable TrackSection bounds
            // when frames are present, see Recording.cs ~line 421) past the sealed
            // identity-loss UT, moving the destroyed-explosion timing in
            // GhostPlaybackLogic. The cache refresh + terminal-engine events would
            // similarly overwrite the sealed terminal payload.
            foreach (var kvp in loadedStates)
            {
                var state = kvp.Value;
                if (IsBackgroundRecordingDestroyed(state.recordingId))
                {
                    ParsekLog.VerboseRateLimited("BgRecorder",
                        $"destroyed-skip.finalize_loaded.{state.vesselPid}",
                        $"FinalizeAllForCommit: skipping destroyed loaded recording " +
                        $"pid={state.vesselPid} rec={state.recordingId}",
                        60.0);
                    continue;
                }
                Vessel v = FlightRecorder.FindVesselByPid(state.vesselPid);
                RecordingFinalizationCache previous = null;
                finalizationCaches.TryGetValue(state.vesselPid, out previous);
                if (v != null)
                {
                    RefreshFinalizationCacheForVessel(
                        v,
                        state.recordingId,
                        FinalizationCacheOwner.BackgroundLoaded,
                        "background_commit_loaded",
                        force: true);
                }
                else
                {
                    if (previous != null)
                    {
                        TryTouchSkippedFinalizationCache(
                            previous,
                            commitUT,
                            "background_commit_missing_vessel",
                            previous.LastObservedOrbitDigest,
                            requiresPeriodicRefresh: false);
                    }
                }

                double closeUT = ResolveBackgroundCommitTrackCloseUT(
                    state,
                    commitUT,
                    v,
                    previous);
                CloseBackgroundTrackSection(state, closeUT);

                Recording flushRec;
                if (tree.Recordings.TryGetValue(state.recordingId, out flushRec))
                {
                    FlushTrackSectionsToRecording(state, flushRec);
                    // A loaded vessel's last adaptive sample can be seconds old at the commit.
                    // closeUT is commitUT whenever the vessel is live (ResolveBackgroundCommitTrackCloseUT).
                    SampleBackgroundEndPoint(v, flushRec, closeUT, "background_commit_loaded");

                    var terminalEvents = FlightRecorder.EmitTerminalEngineAndRcsEvents(
                        state.activeEngineKeys, state.activeRcsKeys, state.activeRoboticKeys,
                        state.lastRoboticPosition, commitUT, "BgRecorder");
                    if (terminalEvents.Count > 0)
                    {
                        flushRec.PartEvents.AddRange(terminalEvents);
                        // FlushTrackSectionsToRecording only marks dirty when
                        // state.trackSections.Count > 0 — a background vessel
                        // finalized with no accumulated sections (e.g. one that
                        // just entered loaded state) would otherwise leave
                        // flushRec.FilesDirty false after this terminal emit.
                        flushRec.MarkFilesDirty();
                    }
                }
            }

            // Update ExplicitEndUT on all background recordings (skip destroyed —
            // their ExplicitEndUT was sealed at MarkDestroyedAtTerminal time and
            // moving it here would shift the non-debris explosion timing in
            // GhostPlaybackLogic from the identity-loss UT to commitUT).
            foreach (var kvp in tree.BackgroundMap)
            {
                Recording treeRec;
                if (tree.Recordings.TryGetValue(kvp.Value, out treeRec) && !treeRec.VesselDestroyed)
                {
                    treeRec.ExplicitEndUT = commitUT;
                    DebrisRelativeRecorderPolicy.NormalizeParentAnchoredRelativeRecording(
                        treeRec,
                        $"FinalizeAllForCommit pid={kvp.Key}");
                }
            }

            ParsekLog.Info("BgRecorder", $"FinalizeAllForCommit complete at UT={commitUT:F1} " +
                $"({onRailsStates.Count} on-rails, {loadedStates.Count} loaded)");
        }

        private static double ResolveBackgroundCommitTrackCloseUT(
            BackgroundVesselState state,
            double commitUT,
            Vessel vessel,
            RecordingFinalizationCache cache)
        {
            if (vessel != null || state == null || !IsPotentiallyApplicableFinalizationCache(cache))
                return commitUT;

            if (!IsFiniteUT(state.lastRecordedUT) || state.lastRecordedUT < 0.0)
                return commitUT;

            if (!IsFiniteUT(commitUT) || state.lastRecordedUT <= commitUT)
                return state.lastRecordedUT;

            return commitUT;
        }

        private static bool IsPotentiallyApplicableFinalizationCache(
            RecordingFinalizationCache cache)
        {
            return RecordingFinalizationCacheProducer.IsPotentiallyApplicableCache(cache);
        }

        private static bool IsFiniteUT(double value)
        {
            return !double.IsNaN(value) && !double.IsInfinity(value);
        }

        private static bool IsConfirmedDestroyedBackgroundEnd(BackgroundRecordingEndReason reason)
        {
            return reason == BackgroundRecordingEndReason.VesselDestroyedOrDespawned;
        }

        internal bool TryApplyFinalizationCacheForBackgroundEnd(
            Recording recording,
            uint vesselPid,
            double endUT,
            string consumerPath,
            bool allowStale,
            bool requireDestroyedTerminal,
            bool confirmedDestroyed,
            out RecordingFinalizationCacheApplyResult result)
        {
            return TryApplyFinalizationCacheForBackgroundEnd(
                recording,
                vesselPid,
                endUT,
                consumerPath,
                allowStale,
                requireDestroyedTerminal,
                confirmedDestroyed,
                retainPredictedTailPastEnd: true,
                out result);
        }

        /// <param name="retainPredictedTailPastEnd">True for every background close that
        /// stops WATCHING a vessel (debris time limit, unload, parent close, confirmed
        /// destruction): a predicted Destroyed / Landed / Splashed ending past
        /// <paramref name="endUT"/> keeps its terminal UT and tail (owner rulings Q1a / Q1b).
        /// False only where the vessel goes on being recorded by another recording, so a
        /// predicted tail past the close would overlap it (the switch-close stamp).</param>
        internal bool TryApplyFinalizationCacheForBackgroundEnd(
            Recording recording,
            uint vesselPid,
            double endUT,
            string consumerPath,
            bool allowStale,
            bool requireDestroyedTerminal,
            bool confirmedDestroyed,
            bool retainPredictedTailPastEnd,
            out RecordingFinalizationCacheApplyResult result)
        {
            result = default(RecordingFinalizationCacheApplyResult);
            if (recording == null)
                return false;

            RecordingFinalizationCache cache = GetFinalizationCacheForRecording(recording);
            if (cache == null && vesselPid != 0
                && finalizationCaches.TryGetValue(vesselPid, out RecordingFinalizationCache pidLookup))
            {
                cache = CopyAndAlignFinalizationCacheIdentity(pidLookup, recording);
            }

            return TryApplyFinalizationCacheForBackgroundEnd(
                recording,
                cache,
                vesselPid,
                endUT,
                consumerPath,
                allowStale,
                requireDestroyedTerminal,
                confirmedDestroyed,
                retainPredictedTailPastEnd,
                out result);
        }

        /// <summary>
        /// Read-only peek at the terminal verdict the
        /// <see cref="TryApplyFinalizationCacheForBackgroundEnd(Recording, uint, double, string, bool, bool, bool, out RecordingFinalizationCacheApplyResult)"/>
        /// overload WOULD apply for this recording, using the identical cache
        /// resolution (recording lookup, then PID fallback) and the identical
        /// <c>ScopeFinalizationCacheToBackgroundEnd</c> clamp. Mutates nothing.
        /// <para>
        /// Exists so a caller can VETO the apply on the cache's own verdict
        /// before it lands. The one caller today is the switch-close terminal
        /// stamp (<c>ParsekFlight.StampTerminalOnSwitchClosedBgOrigin</c>),
        /// which must not seal a provably-alive vessel with a PREDICTED
        /// <see cref="TerminalState.Destroyed"/> from a ballistic-extrapolator
        /// cache. Its terminal UT is at or after the switch UT, so the applier's
        /// own <c>RejectedTerminalBeforeLastSample</c> guard cannot catch it.
        /// </para>
        /// Returns false (with <paramref name="terminal"/> null) when no cache
        /// resolves or the cache carries no terminal.
        /// </summary>
        internal bool TryPeekFinalizationCacheTerminalForBackgroundEnd(
            Recording recording,
            uint vesselPid,
            double endUT,
            out TerminalState? terminal)
        {
            terminal = null;
            if (recording == null)
                return false;

            RecordingFinalizationCache cache = GetFinalizationCacheForRecording(recording);
            if (cache == null && vesselPid != 0
                && finalizationCaches.TryGetValue(vesselPid, out RecordingFinalizationCache pidLookup))
            {
                cache = CopyAndAlignFinalizationCacheIdentity(pidLookup, recording);
            }

            if (cache == null)
                return false;

            terminal = PeekFinalizationCacheTerminalForBackgroundEnd(cache, endUT);
            return terminal.HasValue;
        }

        /// <summary>
        /// Explicit-cache half of
        /// <see cref="TryPeekFinalizationCacheTerminalForBackgroundEnd"/>, mirroring
        /// the two-overload shape of the applier. Returns the terminal the apply
        /// WOULD write for <paramref name="cache"/> at <paramref name="endUT"/>,
        /// after the same background-end scoping clamp. Mutates nothing.
        /// </summary>
        internal static TerminalState? PeekFinalizationCacheTerminalForBackgroundEnd(
            RecordingFinalizationCache cache,
            double endUT)
        {
            return ScopeFinalizationCacheToBackgroundEnd(cache, endUT)?.TerminalState;
        }

        internal bool TryApplyFinalizationCacheForBackgroundEnd(
            Recording recording,
            RecordingFinalizationCache cache,
            uint vesselPid,
            double endUT,
            string consumerPath,
            bool allowStale,
            bool requireDestroyedTerminal,
            bool confirmedDestroyed,
            out RecordingFinalizationCacheApplyResult result)
        {
            return TryApplyFinalizationCacheForBackgroundEnd(
                recording,
                cache,
                vesselPid,
                endUT,
                consumerPath,
                allowStale,
                requireDestroyedTerminal,
                confirmedDestroyed,
                retainPredictedTailPastEnd: true,
                out result);
        }

        internal bool TryApplyFinalizationCacheForBackgroundEnd(
            Recording recording,
            RecordingFinalizationCache cache,
            uint vesselPid,
            double endUT,
            string consumerPath,
            bool allowStale,
            bool requireDestroyedTerminal,
            bool confirmedDestroyed,
            bool retainPredictedTailPastEnd,
            out RecordingFinalizationCacheApplyResult result)
        {
            result = default(RecordingFinalizationCacheApplyResult);
            if (recording == null)
                return false;

            if (cache == null)
            {
                ParsekLog.Verbose("FinalizerCache",
                    $"Background apply skipped: consumer={consumerPath ?? "(null)"} " +
                    $"rec={recording.DebugName} pid={vesselPid} reason=no-cache");
                return false;
            }

            RecordingFinalizationCache scopedCache =
                ScopeFinalizationCacheToBackgroundEnd(cache, endUT, retainPredictedTailPastEnd);
            ApplyFinalizationCacheIdentity(scopedCache, recording);
            LogBackgroundEndScope(cache, scopedCache, recording, vesselPid, endUT, consumerPath,
                retainPredictedTailPastEnd);

            if (requireDestroyedTerminal && scopedCache.TerminalState != TerminalState.Destroyed)
            {
                ParsekLog.Verbose("FinalizerCache",
                    $"Background apply skipped: consumer={consumerPath ?? "(null)"} " +
                    $"rec={recording.DebugName} pid={vesselPid} reason=non-destroyed-cache " +
                    $"terminal={scopedCache.TerminalState?.ToString() ?? "(null)"} " +
                    "using live destruction fallback");
                return false;
            }

            TryClampDestroyedBackgroundTerminalAfterAuthoredData(
                recording,
                scopedCache,
                vesselPid,
                endUT,
                consumerPath,
                confirmedDestroyed);

            var options = new RecordingFinalizationCacheApplyOptions
            {
                ConsumerPath = consumerPath,
                AllowStale = allowStale,
                DestructionConfirmed = confirmedDestroyed
            };

            bool applied = RecordingFinalizationCacheApplier.TryApply(
                recording,
                scopedCache,
                options,
                out result);

            if (applied)
            {
                ParsekLog.Info("BgRecorder",
                    string.Format(
                        CultureInfo.InvariantCulture,
                        "Finalization source=cache consumer={0} rec={1} terminal={2} " +
                        "terminalUT={3:F3} endUT={4:F3} appendedSegments={5}",
                        consumerPath ?? "(null)",
                        recording.DebugName,
                        result.TerminalState?.ToString() ?? "(null)",
                        result.TerminalUT,
                        endUT,
                        result.AppendedSegmentCount));
            }

            return applied;
        }

        private static bool TryClampDestroyedBackgroundTerminalAfterAuthoredData(
            Recording recording,
            RecordingFinalizationCache scopedCache,
            uint vesselPid,
            double endUT,
            string consumerPath,
            bool confirmedDestroyed)
        {
            double lastAuthoredUT = double.NaN;
            if (!confirmedDestroyed)
            {
                LogBackgroundTerminalClampSkipped(recording, scopedCache, vesselPid, endUT, consumerPath,
                    confirmedDestroyed, "not-confirmed-destroyed", lastAuthoredUT);
                return false;
            }

            if (recording == null || scopedCache == null)
            {
                return false;
            }

            if (scopedCache.TerminalState != TerminalState.Destroyed)
            {
                LogBackgroundTerminalClampSkipped(recording, scopedCache, vesselPid, endUT, consumerPath,
                    confirmedDestroyed, "non-destroyed-terminal", lastAuthoredUT);
                return false;
            }

            if (!IsFiniteUT(scopedCache.TerminalUT))
            {
                LogBackgroundTerminalClampSkipped(recording, scopedCache, vesselPid, endUT, consumerPath,
                    confirmedDestroyed, "invalid-terminal-ut", lastAuthoredUT);
                return false;
            }

            if (!RecordingFinalizationCacheApplier.TryGetLastAuthoredUT(recording, out lastAuthoredUT))
            {
                LogBackgroundTerminalClampSkipped(recording, scopedCache, vesselPid, endUT, consumerPath,
                    confirmedDestroyed, "no-authored-data", lastAuthoredUT);
                return false;
            }

            if (scopedCache.TerminalUT + RecordingFinalizationCacheApplier.UtEpsilon >= lastAuthoredUT)
            {
                LogBackgroundTerminalClampSkipped(recording, scopedCache, vesselPid, endUT, consumerPath,
                    confirmedDestroyed, "terminal-not-before-last-authored", lastAuthoredUT);
                return false;
            }

            if (IsFiniteUT(endUT) && lastAuthoredUT > endUT + RecordingFinalizationCacheApplier.UtEpsilon)
            {
                ParsekLog.Warn("FinalizerCache",
                    string.Format(
                        CultureInfo.InvariantCulture,
                        "Background terminal clamp skipped: consumer={0} rec={1} pid={2} " +
                        "reason=LastAuthoredAfterEndUT oldTerminalUT={3:F3} lastAuthoredUT={4:F3} endUT={5:F3} " +
                        "cacheStatus={6} owner={7} confirmedDestroyed={8}",
                        consumerPath ?? "(null)",
                        recording.DebugName,
                        vesselPid,
                        scopedCache.TerminalUT,
                        lastAuthoredUT,
                        endUT,
                        scopedCache.Status,
                        scopedCache.Owner,
                        confirmedDestroyed));
                return false;
            }

            double oldTerminalUT = scopedCache.TerminalUT;
            scopedCache.TerminalUT = lastAuthoredUT;
            if (IsFiniteUT(scopedCache.TailStartsAtUT)
                && scopedCache.TailStartsAtUT > scopedCache.TerminalUT)
            {
                scopedCache.TailStartsAtUT = scopedCache.TerminalUT;
            }

            ParsekLog.Info("FinalizerCache",
                string.Format(
                    CultureInfo.InvariantCulture,
                    "Background terminal clamped: consumer={0} rec={1} pid={2} " +
                    "oldTerminalUT={3:F3} newTerminalUT={4:F3} lastAuthoredUT={5:F3} endUT={6:F3} " +
                    "cacheStatus={7} owner={8} confirmedDestroyed={9}",
                    consumerPath ?? "(null)",
                    recording.DebugName,
                    vesselPid,
                    oldTerminalUT,
                    scopedCache.TerminalUT,
                    lastAuthoredUT,
                    endUT,
                    scopedCache.Status,
                    scopedCache.Owner,
                    confirmedDestroyed));
            return true;
        }

        private static void LogBackgroundTerminalClampSkipped(
            Recording recording,
            RecordingFinalizationCache scopedCache,
            uint vesselPid,
            double endUT,
            string consumerPath,
            bool confirmedDestroyed,
            string reason,
            double lastAuthoredUT)
        {
            ParsekLog.Verbose("FinalizerCache",
                string.Format(
                    CultureInfo.InvariantCulture,
                    "Background terminal clamp skipped: consumer={0} rec={1} pid={2} " +
                    "reason={3} terminal={4} terminalUT={5:F3} lastAuthoredUT={6:F3} endUT={7:F3} " +
                    "cacheStatus={8} owner={9} confirmedDestroyed={10}",
                    consumerPath ?? "(null)",
                    recording != null ? recording.DebugName : "(null)",
                    vesselPid,
                    reason,
                    scopedCache != null && scopedCache.TerminalState.HasValue
                        ? scopedCache.TerminalState.Value.ToString()
                        : "(null)",
                    scopedCache != null ? scopedCache.TerminalUT : double.NaN,
                    lastAuthoredUT,
                    endUT,
                    scopedCache != null ? scopedCache.Status.ToString() : "(null)",
                    scopedCache != null ? scopedCache.Owner.ToString() : "(null)",
                    confirmedDestroyed));
        }

        private static void ApplyFinalizationCacheIdentity(
            RecordingFinalizationCache cache,
            Recording recording)
        {
            if (cache == null || recording == null)
                return;

            if (string.IsNullOrEmpty(cache.RecordingId))
                cache.RecordingId = recording.RecordingId;
            if (cache.VesselPersistentId == 0)
                cache.VesselPersistentId = recording.VesselPersistentId;
        }

        internal static RecordingFinalizationCache ScopeFinalizationCacheToBackgroundEndForTesting(
            RecordingFinalizationCache cache,
            double endUT,
            bool retainPredictedTailPastEnd = true)
        {
            return ScopeFinalizationCacheToBackgroundEnd(cache, endUT, retainPredictedTailPastEnd);
        }

        private static RecordingFinalizationCache ScopeFinalizationCacheToBackgroundEnd(
            RecordingFinalizationCache cache,
            double endUT)
        {
            return ScopeFinalizationCacheToBackgroundEnd(cache, endUT, retainPredictedTailPastEnd: true);
        }

        private static RecordingFinalizationCache ScopeFinalizationCacheToBackgroundEnd(
            RecordingFinalizationCache cache,
            double endUT,
            bool retainPredictedTailPastEnd)
        {
            if (cache == null)
                return null;

            var scoped = new RecordingFinalizationCache
            {
                RecordingId = cache.RecordingId,
                VesselPersistentId = cache.VesselPersistentId,
                Owner = cache.Owner,
                Status = cache.Status,
                CachedAtUT = cache.CachedAtUT,
                CachedAtRealtime = cache.CachedAtRealtime,
                RefreshReason = cache.RefreshReason,
                DeclineReason = cache.DeclineReason,
                LastObservedUT = cache.LastObservedUT,
                LastObservedBodyName = cache.LastObservedBodyName,
                LastSituation = cache.LastSituation,
                LastWasInAtmosphere = cache.LastWasInAtmosphere,
                LastHadMeaningfulThrust = cache.LastHadMeaningfulThrust,
                LastObservedOrbitDigest = cache.LastObservedOrbitDigest,
                TailStartsAtUT = cache.TailStartsAtUT,
                TerminalUT = ResolveBackgroundEndTerminalUT(cache, endUT, retainPredictedTailPastEnd),
                TerminalState = cache.TerminalState,
                TerminalBodyName = cache.TerminalBodyName,
                TerminalOrbit = cache.TerminalOrbit,
                TerminalPosition = cache.TerminalPosition,
                TerrainHeightAtEnd = cache.TerrainHeightAtEnd,
                PredictedSegments = cache.PredictedSegments != null
                    ? new List<OrbitSegment>(cache.PredictedSegments)
                    : new List<OrbitSegment>()
            };

            if (IsFiniteUT(scoped.TerminalUT)
                && (!IsFiniteUT(scoped.TailStartsAtUT)
                    || scoped.TailStartsAtUT > scoped.TerminalUT))
            {
                scoped.TailStartsAtUT = scoped.TerminalUT;
            }

            // A predicted orbit ending pulled back to the close time no longer reaches the
            // conic its TerminalOrbit describes (Q8: often in another sphere of influence).
            // Drop it so the applier stamps the terminal orbit from the last retained segment.
            if (scoped.TerminalOrbit.HasValue
                && IsFiniteUT(scoped.TerminalUT)
                && IsFiniteUT(cache.TerminalUT)
                && scoped.TerminalUT + RecordingFinalizationCacheApplier.UtEpsilon < cache.TerminalUT
                && HasPredictedTailPastEnd(cache, scoped.TerminalUT))
            {
                scoped.TerminalOrbit = null;
                ParsekLog.Verbose("FinalizerCache",
                    string.Format(
                        CultureInfo.InvariantCulture,
                        "Background end scope: dropped far terminal orbit rec={0} pid={1} " +
                        "terminal={2} cacheTerminalUT={3:F3} scopedTerminalUT={4:F3} orbitBody={5}",
                        cache.RecordingId ?? "(null)",
                        cache.VesselPersistentId,
                        cache.TerminalState?.ToString() ?? "(null)",
                        cache.TerminalUT,
                        scoped.TerminalUT,
                        cache.TerminalOrbit.Value.bodyName ?? "(null)"));
            }

            return scoped;
        }

        /// <summary>
        /// True when the cache carries a predicted segment that runs past
        /// <paramref name="endUT"/>: the ending lies ahead on a predicted path (a fall, a
        /// touchdown, or the final orbit of an escape, owner ruling Q8), not at the state the
        /// vessel was observed in.
        /// </summary>
        private static bool HasPredictedTailPastEnd(RecordingFinalizationCache cache, double endUT)
        {
            if (cache?.PredictedSegments == null || !IsFiniteUT(endUT))
                return false;
            for (int i = 0; i < cache.PredictedSegments.Count; i++)
            {
                OrbitSegment segment = cache.PredictedSegments[i];
                if (segment.isPredicted
                    && IsFiniteUT(segment.endUT)
                    && segment.endUT > endUT + RecordingFinalizationCacheApplier.UtEpsilon)
                {
                    return true;
                }
            }
            return false;
        }

        /// <summary>
        /// The terminal UT a background close writes from a cache (owner rulings Q1a / Q1b,
        /// 2026-11-21). A predicted ending the vessel is still on its way to (Destroyed at
        /// impact, Landed / Splashed at touchdown) whose terminal UT is at or after
        /// <paramref name="endUT"/> keeps that terminal UT, so the predicted tail runs past
        /// the moment Parsek stopped watching: a debris time limit, an unload, a parent close
        /// or a stock deletion in the air no longer cuts the fall short. A Destroyed terminal
        /// before <paramref name="endUT"/> keeps the earlier UT (the applier and the
        /// confirmed-destroyed clamp judge it against the authored data). Every other ending
        /// (Orbiting, SubOrbital, Docked, ...) is an observed state and ends at the close
        /// time. With <paramref name="retainPredictedTailPastEnd"/> false (the vessel goes on
        /// being recorded elsewhere) a future predicted ending is pulled back to the close.
        /// </summary>
        internal static double ResolveBackgroundEndTerminalUT(
            RecordingFinalizationCache cache,
            double endUT,
            bool retainPredictedTailPastEnd)
        {
            if (!IsFiniteUT(endUT))
                return cache != null ? cache.TerminalUT : double.NaN;
            if (cache == null || !cache.TerminalState.HasValue)
                return endUT;

            if (retainPredictedTailPastEnd
                && IsFiniteUT(cache.TerminalUT)
                && cache.TerminalUT >= endUT
                && (IsPredictedEndingTerminal(cache.TerminalState.Value)
                    || (IsPredictedOrbitEndingTerminal(cache.TerminalState.Value)
                        && HasPredictedTailPastEnd(cache, endUT))))
            {
                return cache.TerminalUT;
            }

            if (cache.TerminalState.Value == TerminalState.Destroyed
                && IsFiniteUT(cache.TerminalUT))
            {
                return Math.Min(cache.TerminalUT, endUT);
            }

            return endUT;
        }

        private static bool IsPredictedEndingTerminal(TerminalState terminal)
        {
            return terminal == TerminalState.Destroyed
                || terminal == TerminalState.Landed
                || terminal == TerminalState.Splashed;
        }

        // An Orbiting / SubOrbital cache is an observed state unless its ending lies ahead on
        // a predicted path (the final orbit of an escape, owner ruling Q8); the caller checks
        // the predicted segments for that.
        private static bool IsPredictedOrbitEndingTerminal(TerminalState terminal)
        {
            return terminal == TerminalState.Orbiting
                || terminal == TerminalState.SubOrbital;
        }

        private static void LogBackgroundEndScope(
            RecordingFinalizationCache cache,
            RecordingFinalizationCache scopedCache,
            Recording recording,
            uint vesselPid,
            double endUT,
            string consumerPath,
            bool retainPredictedTailPastEnd)
        {
            if (cache == null || scopedCache == null)
                return;

            string decision;
            if (!IsFiniteUT(endUT) || !IsFiniteUT(cache.TerminalUT))
                decision = "unscoped";
            else if (scopedCache.TerminalUT > endUT + RecordingFinalizationCacheApplier.UtEpsilon)
                decision = "kept-predicted-ending-past-end";
            else if (cache.TerminalUT > endUT + RecordingFinalizationCacheApplier.UtEpsilon)
                decision = "pulled-back-to-end";
            else
                decision = "at-or-before-end";

            ParsekLog.Verbose("FinalizerCache",
                string.Format(
                    CultureInfo.InvariantCulture,
                    "Background end scope: consumer={0} rec={1} pid={2} decision={3} terminal={4} " +
                    "cacheTerminalUT={5:F3} scopedTerminalUT={6:F3} endUT={7:F3} segments={8} " +
                    "retainPredictedTailPastEnd={9}",
                    consumerPath ?? "(null)",
                    recording != null ? recording.DebugName : "(null)",
                    vesselPid,
                    decision,
                    cache.TerminalState?.ToString() ?? "(null)",
                    cache.TerminalUT,
                    scopedCache.TerminalUT,
                    endUT,
                    cache.PredictedSegments?.Count ?? 0,
                    retainPredictedTailPastEnd));
        }

        internal RecordingFinalizationCache GetFinalizationCacheForRecording(Recording recording)
        {
            if (recording == null)
                return null;

            RecordingFinalizationCache cache;
            if (recording.VesselPersistentId != 0
                && finalizationCaches.TryGetValue(recording.VesselPersistentId, out cache))
            {
                return CopyAndAlignFinalizationCacheIdentity(cache, recording);
            }

            if (!string.IsNullOrEmpty(recording.RecordingId))
            {
                // RecordingId fallback is only for transitional PID-less recordings at
                // scene-exit commit time; typical tree sizes keep this linear scan small.
                foreach (var kvp in finalizationCaches)
                {
                    cache = kvp.Value;
                    if (cache != null
                        && string.Equals(cache.RecordingId, recording.RecordingId, StringComparison.Ordinal))
                    {
                        return CopyAndAlignFinalizationCacheIdentity(cache, recording);
                    }
                }
            }

            return null;
        }

        private void AdoptInheritedFinalizationCache(
            uint vesselPid,
            string recordingId,
            RecordingFinalizationCache inheritedCache)
        {
            if (inheritedCache == null)
                return;

            inheritedCache.RecordingId = recordingId;
            inheritedCache.VesselPersistentId = vesselPid;
            finalizationCaches[vesselPid] = inheritedCache;

            ParsekLog.Verbose("FinalizerCache",
                $"Inherited active cache for background pid={vesselPid} rec={recordingId ?? "(null)"} " +
                $"owner={inheritedCache.Owner} status={inheritedCache.Status} " +
                $"terminal={inheritedCache.TerminalState?.ToString() ?? "(null)"}");
        }

        private static RecordingFinalizationCache CopyAndAlignFinalizationCacheIdentity(
            RecordingFinalizationCache cache,
            Recording recording)
        {
            if (cache == null)
                return null;

            RecordingFinalizationCache aligned = CopyFinalizationCache(cache);
            if (recording == null)
                return aligned;

            if (string.IsNullOrEmpty(aligned.RecordingId))
                aligned.RecordingId = recording.RecordingId;
            if (aligned.VesselPersistentId == 0)
                aligned.VesselPersistentId = recording.VesselPersistentId;

            return aligned;
        }

        private static RecordingFinalizationCache CopyFinalizationCache(RecordingFinalizationCache source)
        {
            if (source == null)
                return null;

            return new RecordingFinalizationCache
            {
                RecordingId = source.RecordingId,
                VesselPersistentId = source.VesselPersistentId,
                Owner = source.Owner,
                Status = source.Status,
                CachedAtUT = source.CachedAtUT,
                CachedAtRealtime = source.CachedAtRealtime,
                RefreshReason = source.RefreshReason,
                DeclineReason = source.DeclineReason,
                LastObservedUT = source.LastObservedUT,
                LastObservedBodyName = source.LastObservedBodyName,
                LastSituation = source.LastSituation,
                LastWasInAtmosphere = source.LastWasInAtmosphere,
                LastHadMeaningfulThrust = source.LastHadMeaningfulThrust,
                LastObservedOrbitDigest = source.LastObservedOrbitDigest,
                TailStartsAtUT = source.TailStartsAtUT,
                TerminalUT = source.TerminalUT,
                TerminalState = source.TerminalState,
                TerminalBodyName = source.TerminalBodyName,
                TerminalOrbit = source.TerminalOrbit,
                TerminalPosition = source.TerminalPosition,
                TerrainHeightAtEnd = source.TerrainHeightAtEnd,
                PredictedSegments = source.PredictedSegments != null
                    ? new List<OrbitSegment>(source.PredictedSegments)
                    : new List<OrbitSegment>()
            };
        }

        private bool RefreshFinalizationCacheForVessel(
            Vessel vessel,
            string recordingId,
            FinalizationCacheOwner owner,
            string reason,
            bool force)
        {
            if (vessel == null || string.IsNullOrEmpty(recordingId))
                return false;

            double ut = Planetarium.GetUniversalTime();
            uint vesselPid = vessel.persistentId;
            Recording treeRec = null;
            if (tree != null && tree.Recordings != null)
                tree.Recordings.TryGetValue(recordingId, out treeRec);

            BackgroundVesselState loadedState;
            loadedStates.TryGetValue(vesselPid, out loadedState);
            bool hasMeaningfulThrust = loadedState != null
                && RecordingFinalizationCacheProducer.HasMeaningfulThrust(
                    loadedState.activeEngineKeys,
                    loadedState.lastThrottle,
                    loadedState.activeRcsKeys,
                    loadedState.lastRcsThrottle);
            bool inAtmosphere = RecordingFinalizationCacheProducer.IsInAtmosphere(vessel);
            string currentDigest = RecordingFinalizationCacheProducer.BuildVesselDigest(
                vessel,
                hasMeaningfulThrust);

            RecordingFinalizationCache previous;
            finalizationCaches.TryGetValue(vesselPid, out previous);
            bool requiresPeriodicRefresh = hasMeaningfulThrust
                || inAtmosphere
                || RecordingFinalizationCacheProducer.IsSurfaceTerminalSituation(vessel.situation)
                || previous == null
                || previous.Status == FinalizationCacheStatus.Failed;

            if (!RecordingFinalizationCacheProducer.ShouldRefresh(
                    previous != null ? previous.CachedAtUT : double.MinValue,
                    ut,
                    RecordingFinalizationCacheProducer.DefaultRefreshIntervalUT,
                    force,
                    previous?.LastObservedOrbitDigest,
                    currentDigest,
                    requiresPeriodicRefresh))
            {
                TryTouchSkippedFinalizationCache(
                    previous,
                    ut,
                    reason,
                    currentDigest,
                    requiresPeriodicRefresh);
                return false;
            }

            // The finalizer reads recorded points from the recording's sections; a loaded
            // vessel's sections stay in loadedState until a flush, so hand them over too.
            List<TrackSection> unflushedSections = loadedState != null
                && string.Equals(loadedState.recordingId, recordingId, StringComparison.Ordinal)
                ? RecordingFinalizationCacheProducer.CollectUnflushedTrackSections(
                    loadedState.trackSections,
                    loadedState.trackSectionActive,
                    loadedState.currentTrackSection)
                : null;
            RecordingFinalizationCache refreshed;
            bool success = RecordingFinalizationCacheProducer.TryBuildFromLiveVessel(
                treeRec,
                vessel,
                ut,
                owner,
                reason,
                hasMeaningfulThrust,
                tree,
                unflushedSections,
                out refreshed);

            refreshed.RecordingId = recordingId;
            refreshed.VesselPersistentId = vesselPid;
            if (RecordingFinalizationCacheProducer.IsStockOnRailsDeletionRefresh(
                    reason, vessel.packed, vessel.loaded, vessel.situation)
                && !RecordingFinalizationCacheProducer.IsAlreadyClassifiedDestroyedSkip(refreshed))
            {
                // Stock is deleting this vessel on rails: keep the best predicted fall
                // (fresh, else the previous cache's), only then "Destroyed now".
                finalizationCaches[vesselPid] =
                    RecordingFinalizationCacheProducer.ResolveStockOnRailsDeletionCache(
                        previous, refreshed, ut, out string _);
                return success;
            }

            if (!success
                && RecordingFinalizationCacheProducer.IsAlreadyClassifiedDestroyedSkip(refreshed))
            {
                finalizationCaches[vesselPid] = refreshed;
                return false;
            }

            if (!success
                && RecordingFinalizationCacheProducer.TryPreservePreviousCacheAfterFailedRefresh(
                    previous,
                    refreshed,
                    ut,
                    reason,
                    currentDigest))
            {
                finalizationCaches[vesselPid] = previous;
                return false;
            }
            finalizationCaches[vesselPid] = refreshed;
            return success;
        }

        internal static bool TryTouchSkippedFinalizationCache(
            RecordingFinalizationCache previous,
            double currentUT,
            string reason,
            string currentDigest,
            bool requiresPeriodicRefresh)
        {
            if (requiresPeriodicRefresh)
                return false;

            return RecordingFinalizationCacheProducer.TryTouchObservedTerminalCache(
                previous,
                currentUT,
                reason,
                currentDigest);
        }

        /// <summary>
        /// Pure: an on-rails span packed below the atmosphere top keeps the cache the live
        /// vessel produced (the go-on-rails / destroy refreshes) instead of rebuilding it from
        /// the bare conic, as it did before such conics were recorded. Only a missing, empty or
        /// failed cache is rebuilt from the conic, so a background end still finds a tail.
        /// </summary>
        internal static bool ShouldKeepLiveCacheForAtmosphereConic(
            bool packedInsideAtmosphere,
            RecordingFinalizationCache previous)
        {
            return packedInsideAtmosphere
                && previous != null
                && previous.Status != FinalizationCacheStatus.Failed
                && previous.Status != FinalizationCacheStatus.Empty;
        }

        private bool RefreshOnRailsFinalizationCache(
            BackgroundOnRailsState state,
            double currentUT,
            string reason,
            bool force)
        {
            if (state == null || !state.hasOpenOrbitSegment)
                return false;

            string currentDigest = RecordingFinalizationCacheProducer.BuildOrbitSegmentDigest(
                state.currentOrbitSegment);
            RecordingFinalizationCache previous;
            finalizationCaches.TryGetValue(state.vesselPid, out previous);
            if (ShouldKeepLiveCacheForAtmosphereConic(state.packedInsideAtmosphere, previous))
            {
                // The cache built from the live vessel when it packed (chutes, atmosphere,
                // the predicted fall) knows more than the bare conic; the conic itself is
                // being recorded, and the applier starts the cached tail at its end.
                ParsekLog.VerboseRateLimited("FinalizerCache",
                    $"on-rails-atmo-keep-live.{state.vesselPid}",
                    $"On-rails atmosphere conic keeps the live-vessel cache: pid={state.vesselPid} " +
                    $"rec={state.recordingId} reason={reason} cacheStatus={previous.Status} " +
                    $"terminal={previous.TerminalState?.ToString() ?? "(none)"}",
                    30.0);
                return false;
            }
            // Failed caches include already-Destroyed skip caches; refresh them on cadence
            // so the rate-limited skip diagnostic and per-refresh summary remain visible.
            bool requiresPeriodicRefresh = previous == null
                || previous.Status == FinalizationCacheStatus.Failed;
            if (!RecordingFinalizationCacheProducer.ShouldRefresh(
                    previous != null ? previous.CachedAtUT : double.MinValue,
                    currentUT,
                    RecordingFinalizationCacheProducer.DefaultRefreshIntervalUT,
                    force,
                    previous?.LastObservedOrbitDigest,
                    currentDigest,
                    requiresPeriodicRefresh))
            {
                TryTouchSkippedFinalizationCache(
                    previous,
                    currentUT,
                    reason,
                    currentDigest,
                    requiresPeriodicRefresh);
                return false;
            }

            Recording treeRec = null;
            if (tree != null && tree.Recordings != null && !string.IsNullOrEmpty(state.recordingId))
                tree.Recordings.TryGetValue(state.recordingId, out treeRec);

            RecordingFinalizationCache skipped;
            if (RecordingFinalizationCacheProducer.TryBuildAlreadyClassifiedDestroyedSkip(
                    treeRec,
                    state.vesselPid,
                    FinalizationCacheOwner.BackgroundOnRails,
                    currentUT,
                    reason,
                    out skipped))
            {
                skipped.LastObservedOrbitDigest = currentDigest;
                skipped.LastObservedBodyName = state.currentOrbitSegment.bodyName;
                skipped.LastSituation = Vessel.Situations.ORBITING;
                finalizationCaches[state.vesselPid] = skipped;
                RecordingFinalizationCacheProducer.LogRefreshSummary(
                    FinalizationCacheOwner.BackgroundOnRails,
                    reason,
                    treeRec != null ? 1 : 0,
                    1,
                    0,
                    skipped.RecordingId,
                    skipped.VesselPersistentId,
                    skipped.Status,
                    skipped.TerminalState,
                    skipped.DeclineReason,
                    skipped.PredictedSegments?.Count ?? -1);
                return false;
            }

            RecordingFinalizationCache refreshed;
            bool success = RecordingFinalizationCacheProducer.TryBuildFromStableOrbitSegment(
                state.recordingId,
                state.vesselPid,
                state.currentOrbitSegment,
                currentUT,
                FinalizationCacheOwner.BackgroundOnRails,
                reason,
                out refreshed);
            finalizationCaches[state.vesselPid] = refreshed;
            return success;
        }
    }
}
