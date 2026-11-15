using System;
using System.Collections;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using Parsek.Logistics;
using UnityEngine;

namespace Parsek
{
    // Rewind on load: the post-load rewind dispatch, the plain-rewind clock, the Re-Fly marker
    // clear, the rewound-tree ghost-only marking and the rewind spawn suppression.
    public partial class ParsekScenario
    {
        /// <summary>
        /// Phase 6 of Rewind-to-Staging: drains <see cref="RewindInvokeContext"/>
        /// if a re-fly invocation was initiated pre-load. Runs in the new
        /// scenario after the normal OnLoad pipeline has settled — bundle
        /// restore overrides the .sfs-loaded recordings / ledger / scenario
        /// lists with the pre-load in-memory state, as per the §6.4 table.
        /// <para>
        /// Only fires when <c>LoadedScene == FLIGHT</c>; the RP quicksave is
        /// always a flight-scene save. Any other scene means the load took an
        /// unexpected branch — log Error and clear the context.
        /// </para>
        /// <para>
        /// <paramref name="loadedScenarioNode"/> is this OnLoad's node, which on a Re-Fly start
        /// is the RP quicksave's own ParsekScenario (<c>FlightDriver</c> loads the in-memory game
        /// the invoker parsed from that file): the bundle restore reads each kept route's loop
        /// position from its ROUTES.
        /// </para>
        /// </summary>
        private static void DispatchRewindPostLoadIfPending(ConfigNode loadedScenarioNode)
        {
            if (!RewindInvokeContext.Pending) return;

            if (HighLogic.LoadedScene != GameScenes.FLIGHT)
            {
                ParsekLog.Error("Rewind",
                    $"DispatchRewindPostLoadIfPending: pending invocation " +
                    $"but scene is {HighLogic.LoadedScene} (expected FLIGHT) — aborting");
                RewindInvoker.ShowUserError(
                    $"Rewind failed: scene loaded as {HighLogic.LoadedScene} " +
                    "instead of flight");
                RewindInvokeContext.Clear();
                return;
            }

            RewindInvoker.ConsumePostLoad(loadedScenarioNode);
        }

        /// <summary>
        /// Pure: the world UT a plain rewind's timeline break and live-only income drop read.
        /// The loaded clock is the wound-back one (the lead window); the world is the rewind
        /// save's own (<see cref="LedgerHeldWindow.ResolveAtRewind"/>: its own UT raised to the
        /// held-through UT it carries) whenever that lies ahead of it, else
        /// <see cref="ResolvePlainRewindBreakUT"/>.
        /// </summary>
        internal static double ResolvePlainRewindWorldUT(
            double loadedSaveUT, double rewindSaveOwnClockUT, double carriedHeldThroughUT)
        {
            double held = LedgerHeldWindow.ResolveAtRewind(loadedSaveUT, rewindSaveOwnClockUT, carriedHeldThroughUT);
            if (LedgerHeldWindow.IsUsableHeldUT(held))
                return held;
            return ResolvePlainRewindBreakUT(loadedSaveUT, rewindSaveOwnClockUT);
        }

        /// <summary>
        /// Pure: the UT a plain rewind's timeline break records - the loaded game's own clock
        /// (persistent.sfs is the rewind save by then), else the rewind save's clock the rewind
        /// parsed; NaN when neither is known (the break is then not recorded, with a Warn).
        /// </summary>
        internal static double ResolvePlainRewindBreakUT(double loadedSaveUT, double rewindSaveOwnClockUT)
        {
            if (!double.IsNaN(loadedSaveUT) && !double.IsInfinity(loadedSaveUT))
                return loadedSaveUT;
            if (!double.IsNaN(rewindSaveOwnClockUT) && !double.IsInfinity(rewindSaveOwnClockUT))
                return rewindSaveOwnClockUT;
            return double.NaN;
        }

        /// <summary>
        /// Step of <see cref="HandleRewindOnLoad"/>: collects the spawned vessel pids and every
        /// recording's vessel name into the pending-cleanup sets OnFlightReady reads (null when empty).
        /// </summary>
        internal static void HandleRewindOnLoadPhaseCollectCleanupData()
        {
            // Collect spawned vessel PIDs for belt-and-suspenders in OnFlightReady
            var (rewindSpawnedPids, _) = RewindLauncher.CollectSpawnedVesselInfo();
            RewindLauncher.PendingCleanupPids = rewindSpawnedPids.Count > 0 ? rewindSpawnedPids : null;

            // Collect ALL recording vessel names for flightState stripping.
            // On rewind, ANY vessel matching a recording name is from the future
            // and must be stripped — not just those with non-zero SpawnedVesselPersistentId.
            // (PIDs may already be zero from a previous rewind's ResetAllPlaybackState.)
            var allRecordingNames = RecordingStore.CollectAllRecordingVesselNames();
            RewindLauncher.PendingCleanupNames = allRecordingNames.Count > 0 ? allRecordingNames : null;
            ParsekLog.Info("Rewind",
                $"OnLoad: rewind cleanup data set — " +
                $"{rewindSpawnedPids.Count} pid(s), {allRecordingNames.Count} name(s)");
        }

        /// <summary>
        /// Step of <see cref="HandleRewindOnLoad"/>: resets the playback state of every recording
        /// and tree (keeping the historical spawns the pre-load strip kept), then marks only the rewound
        /// active/source recording as spawn-suppressed.
        /// </summary>
        internal static void HandleRewindOnLoadPhaseResetPlaybackAndProtectSource(IReadOnlyList<Recording> recordings)
        {
            // Reset ALL playback state (recordings + trees), except the spawn state of
            // committed history whose vessel the pre-load strip kept (an earlier tree's
            // spawned vessel that no replaying recording re-produces).
            var (standaloneCount, treeCount) = RewindLauncher.ResetAllPlaybackState(
                RewindContext.RewindHistoricalSpawnKeepRecordingIds);
            ParsekLog.Info("Rewind",
                $"OnLoad: resetting playback state for {standaloneCount} recordings + {treeCount} trees");

            // Mark only the active/source recording that plain Rewind-to-Launch stripped
            // from flightState. Same-tree future recordings remain spawn-eligible so
            // their normal terminal materialization can happen after playback reaches
            // their EndUT (#589), while #573 still blocks the source duplicate.
            int protectedCount = MarkRewoundTreeRecordingsAsGhostOnly(recordings);
            ParsekLog.Info("Rewind",
                $"OnLoad: SpawnSuppressedByRewind scope evaluated — " +
                $"protectedActiveSource={protectedCount}; same-tree future recordings remain spawn-eligible (#573/#589)");
        }

        /// <summary>
        /// Step of <see cref="HandleRewindOnLoad"/>: strips the future vessels from the loaded
        /// flightState (launch-identity match, then the PRELAUNCH pid-whitelist pass), clears the
        /// pending-cleanup sets so OnFlightReady does not strip again, and drops the quicksave pids.
        /// </summary>
        internal static void HandleRewindOnLoadPhaseStripFutureVessels()
        {
            // Strip vessels belonging to a recording's launch from flightState.
            // The rewind save was preprocessed to strip the recorded vessel,
            // but KSP's scene transition may reintroduce vessels from the old
            // persistent.sfs. On rewind every vessel that belongs to a recording's
            // launch (its recorded source OR its spawn/adoption endpoint) is from the
            // future and is removed.
            //
            // BUG-H: launch-identity-aware (pid + launch-Guid), so a DIFFERENT real
            // launch that merely reuses the craft-baked name/pid is never deleted.
            // requireWhitelist=false preserves the rewind contract: genuinely
            // future-timeline vessels are still removed (the launch-identity match here
            // plus the separate StripFuturePrelaunchVessels pid-whitelist pass below).
            // The rewind quicksave pids are passed as a protect-set backstop so a vessel
            // that pre-existed the rewind point (present in the quicksave) is never deleted
            // even for the rare guidless adoption-stamp recording where the Guid gate alone
            // would fall back to pid-only. When the quicksave pids are unavailable this is
            // null and the strip falls back to identity-only (its prior behaviour).
            {
                var flightState = HighLogic.CurrentGame?.flightState;
                if (flightState != null)
                {
                    // A foreign vessel a tree adopted (inventory transfer, or a board / dock
                    // onto it) existed before that tree: its member and adoption holders
                    // never mark it as future.
                    var allCommitted = RecordingStore.CollectAllCommittedRecordings();
                    var stripCandidates = ForeignInventoryTransfer.ExcludeForeignAdoptions(
                        allCommitted,
                        ForeignInventoryTransfer.CollectForeignAdoptedVesselMemberRecordingIds(
                            RecordingStore.CommittedTrees),
                        out int foreignAdoptionsExcluded);
                    if (foreignAdoptionsExcluded > 0)
                        ParsekLog.Info("Rewind",
                            $"OnLoad strip: {foreignAdoptionsExcluded} recording(s) of foreign vessels adopted " +
                            "by inventory transfer or a board / dock left out of the launch-identity match");
                    StripOrphanedSpawnedVessels(
                        flightState.protoVessels, stripCandidates,
                        matchSource: true, skipPrelaunch: false,
                        requireWhitelist: false,
                        preExistingWhitelist: RewindContext.RewindQuicksaveVesselPids);
                }
            }

            // Rewind strip already handled protoVessel cleanup in flightState.
            // Clear pending data so OnFlightReady doesn't re-run with overbroad names
            // that would match freshly-spawned past vessels (bug #134). The revert path's
            // alreadyHasCleanupData guard (line ~352) will see null and collect
            // fresh data from CollectSpawnedVesselInfo() if needed.
            RewindLauncher.ClearPendingCleanupAfterRewindStrip();

            // Strip PRELAUNCH vessels from the future (bug #129).
            // StripOrphanedSpawnedVessels filters by name — unrecorded PRELAUNCH vessels
            // (e.g. a pad vessel from a later launch) fail the name check and survive.
            // Use the quicksave PID whitelist to identify and remove them.
            var quicksavePids = RewindContext.RewindQuicksaveVesselPids;
            if (quicksavePids != null)
            {
                var fs = HighLogic.CurrentGame?.flightState;
                if (fs != null)
                {
                    int prelaunchStripped = StripFuturePrelaunchVessels(fs.protoVessels, quicksavePids);
                    if (prelaunchStripped > 0)
                        ParsekLog.Info("Rewind",
                            $"Stripped {prelaunchStripped} future vessel(s) not in quicksave whitelist");
                }
                RewindContext.SetQuicksaveVesselPids(null);
            }
        }

        /// <summary>
        /// Handles the rewind (go-back) path during OnLoad. Restores milestone state,
        /// collects cleanup PIDs/names, resets playback state, strips orphaned vessels,
        /// schedules resource adjustment, re-reserves crew, and clears rewind flags.
        /// </summary>
        private void HandleRewindOnLoad(ConfigNode node, IReadOnlyList<Recording> recordings)
        {
            RecorderStateLog.RecState("HandleRewindOnLoad:entry", CaptureScenarioRecorderState());
            ParsekLog.Info("Rewind",
                $"OnLoad: rewind detected, skipping .sfs recording/crew load " +
                $"(using {recordings.Count} in-memory recordings)");
            ClearActiveReFlyMarkerForPlainRewind();

            // Restore milestone mutable state with resetUnmatched=true
            // (milestones created after rewind point get reset to unreplayed)
            MilestoneStore.RestoreMutableState(node, resetUnmatched: true);

            HandleRewindOnLoadPhaseCollectCleanupData();

            HandleRewindOnLoadPhaseResetPlaybackAndProtectSource(recordings);

            HandleRewindOnLoadPhaseStripFutureVessels();

            // Hold back every vessel of the rewind's world the committed future uses after the
            // rewind (rewind design 10.13, flight recorder design 12.5 / 20.3), in every scene.
            ApplyRewindVesselHolds(RewindContext.RewindAdjustedUT);

            // The rewind save's world holds every ledger row up to its own UT, which lies ahead of
            // the wound-back clock by the lead time (todo REWIND-LEAD-WINDOW-KSC-ACTIONS-LAND-IN-THE-FUTURE):
            // until the clock passes it those rows count as reached for the walks below, the
            // committed-future index, the clock crossings and the KSP state patches.
            LedgerHeldWindow.SetAtRewind(
                RewindContext.RewindAdjustedUT,
                RewindContext.RewindSaveOwnClockUT,
                RewindContext.RewindSaveLedgerHeldThroughUT);

            // Defense-in-depth: reconcile spawn state after all strips (#168).
            // ResetAllPlaybackState already zeroed spawn PIDs, so this should be a no-op,
            // but guards against any future code path that restores PIDs before the strip.
            //
            // (pid, launch-guid) IDENTITIES, not bare pids: the strips above REMOVE the
            // stripped ProtoVessels from flightState.protoVessels, so the list is already the
            // survivor set — but a survivor that merely shares a recording's craft-baked
            // SpawnedVesselPersistentId can be a DIFFERENT launch of the same craft, which
            // under a pid-only decision keeps the recording at VesselSpawned=true pointing at
            // a stranger and permanently blocks its terminal spawn. Same collector and same
            // guid-aware overload as the revert path above and the Re-Fly path in
            // RewindInvoker.ReconcilePostStripSpawnState.
            {
                var fsReconcile = HighLogic.CurrentGame?.flightState;
                if (fsReconcile != null)
                    ReconcileSpawnStateAfterStrip(
                        CollectSurvivingVesselIdentities(fsReconcile.protoVessels), recordings);
            }

            // Rescue crew orphaned by vessel stripping (#116).
            if (HighLogic.CurrentGame?.flightState != null)
                CrewReservationManager.RescueOrphanedCrew(
                    HighLogic.CurrentGame.flightState.protoVessels);

            // Schedule resource + UT adjustment (deferred — singletons from the OLD
            // scene may still be alive during OnLoad; we must yield at least one frame
            // so the new scene's Funding/R&D/Reputation/Planetarium are initialized).
            // Setting UT before LoadScene does NOT work — scene transition overwrites it.
            RecordingStore.RewindUTAdjustmentPending = true;
            RecordingStore.RewindUTAdjustmentTargetUT = RewindContext.RewindAdjustedUT;
            // Drawdown-guard signal 5 (Blocker 1): arm BEFORE scheduling the coroutine so
            // it is true before EndRewind() below clears IsRewinding. The coroutine's
            // try/finally around the deferred RecalculateAndPatch clears it (authoritative);
            // the next scene's OnAwake clears it as a race-free fail-safe if the host is
            // destroyed mid-wait. This authorizes the deferred plain-rewind drawdown, which
            // runs after all four other signals are false.
            RewindContext.BeginRewindResourceAdjustment();
            StartCoroutine(ApplyRewindResourceAdjustment());
            ParsekLog.Info("Rewind",
                "OnLoad: resource + UT adjustment deferred (waiting for new scene singletons)");

            // Route event log + store reconciliation, the Re-Fly seam's mirror
            // (RouteLoadReconcile.ReconcileAtGoBackRewind: retire route events after the cutoff in
            // place, the shared RouteRewindClassifier.ReconcileStoreAtRewind, then each kept
            // route's loop position back from the rewind save's own route copy, which this
            // OnLoad node is not: it is persistent.sfs, so ExecuteRewindSaveLoad read the copy
            // from the parsed save). The route cutoff is that save's own UT, before the lead-time
            // windback, so the events kept and the route state restored describe the same moment;
            // the career prune and recalc below stay at RewindAdjustedUT. Removes events only and
            // emits none (OnLoad-safe).
            Logistics.RouteLoadReconcile.ReconcileAtGoBackRewind(
                RewindContext.RewindAdjustedUT,
                RewindContext.RewindSaveRoutes,
                RewindContext.RewindSaveClockUT,
                RewindContext.RewindSaveOwnClockUT,
                RewindContext.RewindSaveEvents);
            Logistics.RouteEventLog.LogWalkSummary();

            // Restore career state to the rewind target. The cutoff walk keeps
            // funds/science/tech at the rewind's career cutoff; LedgerOrchestrator then
            // reprojects crew reservations from the full committed timeline so
            // future recorded crew remain unavailable for new missions.
            // RewindContext.RewindAdjustedUT is still populated at this point;
            // EndRewind() below clears it. The deferred coroutine captures the
            // same value into a local BEFORE its yield so its second call is
            // independent of RewindContext state.
            //
            // The career cutoff is the wound-back clock raised to the ledger held-through UT
            // set above (the rewind save's own UT), resolved explicitly here: a launch within
            // the lead time of career start winds the clock to exactly 0, which the live-clock
            // floor treats as a clock that is not ready and does not raise
            // (todo REWIND-PRUNE-AND-WALK-AT-UT-ZERO).
            double rewindCareerCutoffUT = LedgerHeldWindow.ResolveRewindCutoffUT(RewindContext.RewindAdjustedUT);
            ParsekLog.Info("Rewind",
                "OnLoad: career cutoff UT " + rewindCareerCutoffUT.ToString("R", CultureInfo.InvariantCulture) +
                " (woundBackClock=" + RewindContext.RewindAdjustedUT.ToString("R", CultureInfo.InvariantCulture) +
                " rewindSaveUT=" + RewindContext.RewindSaveOwnClockUT.ToString("R", CultureInfo.InvariantCulture) +
                ") for the baseline prune and the career walk");
            GameStateStore.PruneBaselinesAfterUT(rewindCareerCutoffUT);
            LedgerOrchestrator.RecalculateAndPatch(
                rewindCareerCutoffUT,
                suppressSuspiciousDrawdownWarnings: true);

            // Clear rewind flags — rewind loads into SpaceCenter, not Flight
            ParsekLog.Info("Rewind",
                $"OnLoad: rewind complete at UT {RewindContext.RewindUT}. " +
                $"Timeline: {recordings.Count} recordings");
            // Any pending recovery-funds callbacks deferred before the rewind cannot
            // pair after the boundary — stock already fired (or will never fire) the
            // paired FundsChanged(VesselRecovery) event relative to the old timeline.
            LedgerOrchestrator.FlushStalePendingRecoveryFunds("rewind end");
            RecoveryPayoutContextStore.Clear("rewind end");
            RewindContext.EndRewind();
            RecorderStateLog.RecState("HandleRewindOnLoad:exit", CaptureScenarioRecorderState());
        }

        /// <summary>
        /// THE marker-clearing helper: drops <see cref="ActiveReFlySessionMarker"/> and, in the
        /// same breath, the transient <see cref="ReFlyProvisionalRetirement"/> hand-over note.
        ///
        /// <para>
        /// The note is a same-process slot naming a provisional a prune pass retired, meant to be
        /// consumed by that session's conclusion. Its own session gate cannot protect it once the
        /// marker is gone: a later marker can legitimately carry the SAME SessionId (an F9 re-arms
        /// it), so a note left behind by a session that ENDED is adoptable by the next one. Two
        /// clear sites paired the drop by hand and the remaining nine did not, so the pairing
        /// lives here and every session-ending clear routes through it. The residual was a leaked
        /// reference rather than a wrong decision on any established route - this is hardening,
        /// not a bug fix.
        /// </para>
        /// <para>
        /// Deliberately NOT routed through here: <c>LoadRewindStagingState</c>'s reset. That runs
        /// on every OnLoad and the marker is repopulated from the just-loaded node immediately
        /// after, while the note is SUPPOSED to survive the scene change between the scene-exit
        /// finalize that wrote it and the merge-dialog answer that consumes it. Clearing it there
        /// would break the one route the note exists for.
        /// </para>
        /// </summary>
        internal void ClearActiveReFlySessionMarker(string reason)
        {
            ActiveReFlySessionMarker = null;
            // The T4 pairing (every marker clear also clears the render session's anchor
            // epsilon map, or stale epsilons leak into post-session renders) now lives here
            // too, so one helper owns everything that dies with the session and no site can
            // half-remember. Callers pass the reason both logs carry.
            Parsek.Rendering.RenderSessionState.Clear(reason);
            ReFlyProvisionalRetirement.Clear(reason);
        }

        /// <summary>
        /// Clears a loaded active Re-Fly marker while the plain rewind OnLoad branch resumes replay.
        /// </summary>
        internal bool ClearActiveReFlyMarkerForPlainRewind()
        {
            var marker = ActiveReFlySessionMarker;
            if (marker == null)
                return false;

            string sessionId = marker.SessionId ?? "<no-id>";
            string activeRecordingId = marker.ActiveReFlyRecordingId ?? "<no-id>";
            string originRecordingId = marker.OriginChildRecordingId ?? "<no-id>";
            string rewindPointId = marker.RewindPointId ?? "<no-rp>";

            // This discards only stale session state; supersede caches do not
            // depend on the marker and should not be bumped for this cleanup.
            ClearActiveReFlySessionMarker("plain-rewind");
            // Plain rewind loads before FlightDriver state is ready, so Apply is
            // normally a logged no-op here. Keep it paired with marker clears.
            ReFlyRevertButtonGate.Apply("PlainRewind:clear-refly-marker");
            ParsekLog.Info("Rewind",
                "OnLoad: cleared stale active Re-Fly marker during plain rewind " +
                $"sess={sessionId} active={activeRecordingId} origin={originRecordingId} rp={rewindPointId}");
            return true;
        }

        /// <summary>
        /// After plain Rewind-to-Launch, mark only the active/source recording that was
        /// stripped from flightState as <see cref="Recording.SpawnSuppressedByRewind"/>.
        /// This preserves #573's duplicate-source protection without turning every
        /// future same-tree recording into permanent ghost-only history (#589).
        ///
        /// <para>Same-tree future recordings are logged and deliberately left
        /// spawn-eligible. A marker left by an earlier rewind is cleared when it no
        /// longer matches this rewind's active/source scope.</para>
        /// </summary>
        internal static int MarkRewoundTreeRecordingsAsGhostOnly(
            IReadOnlyList<Recording> recordings)
        {
            if (recordings == null || recordings.Count == 0)
                return 0;

            uint rewindSourcePid = SceneEntryVesselScope.RewindReplayTargetSourcePid;
            string rewindRecId = SceneEntryVesselScope.RewindReplayTargetRecordingId;
            string rewoundTreeId = ResolveRewoundTreeId(recordings, rewindRecId);
            string rewindRecGuid = ResolveRewoundRecordingGuid(recordings, rewindRecId);
            double rewindUT = RewindContext.RewindUT;

            int marked = 0;
            int retained = 0;
            int futureAllowed = 0;
            int cleared = 0;
            int skipped = 0;
            for (int i = 0; i < recordings.Count; i++)
            {
                var rec = recordings[i];
                if (rec == null)
                {
                    skipped++;
                    continue;
                }

                bool inRewoundTree = rewoundTreeId != null
                    && string.Equals(rec.TreeId, rewoundTreeId, StringComparison.Ordinal);
                bool sameTreeFuture = inRewoundTree && IsFutureOfRewindTarget(rec, rewindUT);

                if (ShouldApplyRewindSpawnSuppression(
                        rec,
                        rewindRecId,
                        rewindSourcePid,
                        rewoundTreeId,
                        rewindUT,
                        out string applyReason,
                        rewindRecGuid))
                {
                    if (!rec.SpawnSuppressedByRewind)
                        marked++;
                    else
                        retained++;

                    rec.SpawnSuppressedByRewind = true;
                    rec.SpawnSuppressedByRewindReason = applyReason;
                    rec.SpawnSuppressedByRewindUT = rewindUT;

                    ParsekLog.Verbose("Rewind",
                        $"SpawnSuppressedByRewind applied: #{i} \"{rec.VesselName}\" " +
                        $"id={rec.RecordingId} tree={(rec.TreeId ?? "<none>")} " +
                        $"reason={applyReason} rewindUT={FormatRewindUT(rewindUT)} " +
                        $"startUT={FormatRewindUT(rec.StartUT)} endUT={FormatRewindUT(rec.EndUT)} " +
                        "(#573 active/source recording protection)");
                    continue;
                }

                if (sameTreeFuture)
                {
                    futureAllowed++;
                    if (rec.SpawnSuppressedByRewind)
                    {
                        ClearRewindSpawnSuppression(
                            rec,
                            $"reason={RewindSpawnSuppressionReasonSameTreeFutureRecording} " +
                            $"rewindUT={FormatRewindUT(rewindUT)} startUT={FormatRewindUT(rec.StartUT)} " +
                            $"endUT={FormatRewindUT(rec.EndUT)}",
                            "future-of-rewind same-tree recording remains spawn-eligible");
                        cleared++;
                    }

                    ParsekLog.Verbose("Rewind",
                        $"SpawnSuppressedByRewind not applied: #{i} \"{rec.VesselName}\" " +
                        $"id={rec.RecordingId} tree={rec.TreeId} " +
                        $"reason={RewindSpawnSuppressionReasonSameTreeFutureRecording} " +
                        $"rewindUT={FormatRewindUT(rewindUT)} startUT={FormatRewindUT(rec.StartUT)} " +
                        $"endUT={FormatRewindUT(rec.EndUT)} — spawn allowed when endpoint is reached post-rewind");
                    continue;
                }

                if (rec.SpawnSuppressedByRewind)
                {
                    ClearRewindSpawnSuppression(
                        rec,
                        $"reason=unrelated-to-current-rewind rewindUT={FormatRewindUT(rewindUT)}",
                        "stale marker no longer matches active/source scope");
                    cleared++;
                }
                skipped++;
            }

            ParsekLog.Verbose("Rewind",
                $"SpawnSuppressedByRewind scope summary: marked={marked} retained={retained} " +
                $"cleared={cleared} futureAllowed={futureAllowed} skipped={skipped} " +
                $"rewindRec={rewindRecId ?? "<null>"} tree={rewoundTreeId ?? "<none>"} " +
                $"rewindUT={FormatRewindUT(rewindUT)}");
            return marked + retained;
        }

        internal static bool ShouldApplyRewindSpawnSuppression(
            Recording rec,
            string rewindRecordingId,
            uint rewindSourcePid,
            string rewoundTreeId,
            double rewindUT,
            out string reason,
            string rewindRecordingGuid = null)
        {
            reason = null;
            if (rec == null)
                return false;

            if (!string.IsNullOrEmpty(rewindRecordingId)
                && string.Equals(rec.RecordingId, rewindRecordingId, StringComparison.Ordinal))
            {
                reason = RewindSpawnSuppressionReasonSameRecording;
                return true;
            }

            // #976-class: rewindSourcePid is the craft-baked pid, reused by every launch of the
            // same craft, so a bare pid match would suppress prior UNRELATED launches' terminal
            // spawns (the long-latent #829 hole). Require the candidate to share the rewound
            // launch (guid); a conclusive guid mismatch means a different launch and is not
            // suppressed. A null/unknown guid on either side falls back to today's pid-only match.
            bool matchesRewindSource = rewindSourcePid != 0
                && rec.VesselPersistentId == rewindSourcePid
                && !VesselLaunchIdentity.GuidsConclusivelyDiffer(rec.RecordedVesselGuid, rewindRecordingGuid);
            if (!matchesRewindSource)
                return false;

            bool inRewoundTree = rewoundTreeId != null
                && string.Equals(rec.TreeId, rewoundTreeId, StringComparison.Ordinal);
            if (inRewoundTree && IsFutureOfRewindTarget(rec, rewindUT))
                return false;

            if (string.IsNullOrEmpty(rewoundTreeId))
            {
                reason = RewindSpawnSuppressionReasonSameRecording;
                return true;
            }

            if (RecordingOverlapsRewindTarget(rec, rewindUT))
            {
                reason = RewindSpawnSuppressionReasonSameRecording;
                return true;
            }

            return false;
        }

        internal static bool IsFutureOfRewindTarget(Recording rec, double rewindUT)
        {
            if (rec == null)
                return false;

            return rec.StartUT > rewindUT + RewindSpawnSuppressionUTEpsilon
                && rec.EndUT > rewindUT + RewindSpawnSuppressionUTEpsilon;
        }

        internal static bool RecordingOverlapsRewindTarget(Recording rec, double rewindUT)
        {
            if (rec == null)
                return false;

            return rec.StartUT <= rewindUT + RewindSpawnSuppressionUTEpsilon
                && rec.EndUT >= rewindUT - RewindSpawnSuppressionUTEpsilon;
        }

        internal static void ClearRewindSpawnSuppression(
            Recording rec,
            string reason,
            string lifecycle)
        {
            if (rec == null || !rec.SpawnSuppressedByRewind)
                return;

            string oldReason = string.IsNullOrEmpty(rec.SpawnSuppressedByRewindReason)
                ? "<none>"
                : rec.SpawnSuppressedByRewindReason;
            double oldUT = rec.SpawnSuppressedByRewindUT;

            rec.SpawnSuppressedByRewind = false;
            rec.SpawnSuppressedByRewindReason = null;
            rec.SpawnSuppressedByRewindUT = double.NaN;

            ParsekLog.Info("Rewind",
                $"SpawnSuppressedByRewind cleared: \"{rec.VesselName}\" id={rec.RecordingId} " +
                $"previousReason={oldReason} previousRewindUT={FormatRewindUT(oldUT)} " +
                $"{reason} lifecycle=\"{lifecycle}\"");
        }

        /// <summary>
        /// Clears the #573 active/source spawn-suppression marker when the player
        /// engages with a rewound recording via Watch. The original #573 protection
        /// was scoped at rewind time to avoid background ghost playback materialising
        /// a duplicate of a vessel the player just stripped (chain-tip respawn next to
        /// the player's freshly-launched vessel). Watching the rewound recording is
        /// the player's explicit signal that they want to see this recording's outcome
        /// — the spawn at terminal end should be allowed to evaluate normally.
        /// Returns true when a marker was actually cleared. Only acts on the
        /// <see cref="RewindSpawnSuppressionReasonSameRecording"/> reason, which is
        /// the only marker reason produced today. This helper
        /// deliberately does NOT gate on terminal state — final spawnability is
        /// owned by <c>ShouldSpawnAtRecordingEnd</c>'s other gates (snapshot
        /// situation, terminal-state, PID dedup, etc.). Gating here on an enum
        /// whitelist would re-introduce the bug for null-terminal recordings that
        /// pass <c>ShouldSpawnAtRecordingEnd</c> via the snapshot-situation path.
        /// </summary>
        internal static bool TryClearSpawnSuppressionOnWatchEntry(Recording rec)
        {
            if (rec == null || !rec.SpawnSuppressedByRewind)
                return false;
            if (!string.Equals(rec.SpawnSuppressedByRewindReason,
                    RewindSpawnSuppressionReasonSameRecording,
                    StringComparison.Ordinal))
                return false;

            ClearRewindSpawnSuppression(
                rec,
                $"reason={rec.SpawnSuppressedByRewindReason}",
                "watch-entry: user engaged with rewound recording, allowing spawn at recording end");
            return true;
        }

        internal static string FormatRewindUT(double ut)
        {
            return double.IsNaN(ut)
                ? "<nan>"
                : ut.ToString("F3", CultureInfo.InvariantCulture);
        }

        /// <summary>
        /// Returns the TreeId of the rewind owner recording, or null when the rewind
        /// scope was not a tree (e.g., standalone recording rewind, or scope cleared).
        /// </summary>
        private static string ResolveRewoundTreeId(
            IReadOnlyList<Recording> recordings, string rewindRecordingId)
        {
            if (string.IsNullOrEmpty(rewindRecordingId))
                return null;

            for (int i = 0; i < recordings.Count; i++)
            {
                var rec = recordings[i];
                if (rec != null && string.Equals(rec.RecordingId, rewindRecordingId, StringComparison.Ordinal))
                    return rec.TreeId;
            }
            return null;
        }

        // Launch guid of the rewound recording, so suppression only catches recordings sharing
        // that launch (not other launches of the same craft that reuse the baked pid).
        private static string ResolveRewoundRecordingGuid(
            IReadOnlyList<Recording> recordings, string rewindRecordingId)
        {
            if (string.IsNullOrEmpty(rewindRecordingId))
                return null;

            for (int i = 0; i < recordings.Count; i++)
            {
                var rec = recordings[i];
                if (rec != null && string.Equals(rec.RecordingId, rewindRecordingId, StringComparison.Ordinal))
                    return rec.RecordedVesselGuid;
            }
            return null;
        }
    }
}
