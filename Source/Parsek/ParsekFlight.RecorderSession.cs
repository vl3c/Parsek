using System;
using System.Collections;
using System.Collections.Generic;
using System.Globalization;
using KSP.UI.Screens;
using ToolbarControl_NS;
using UnityEngine;

namespace Parsek
{
    // The recorder session: start, stop, flush and promote, rails and warp transitions, SOI and
    // boundary splits.
    public partial class ParsekFlight
    {
        /// <summary>
        /// Appends the current recorder's accumulated data (points, orbit segments, part events)
        /// to the tree's Recording object. Sorts part events by UT. Sets IsRecording = false
        /// to prevent dangling recorders. Populates VesselPersistentId on the tree Recording.
        /// </summary>
        void FlushRecorderToTreeRecording(FlightRecorder rec, RecordingTree tree)
        {
            if (rec == null || tree == null) return;

            string recId = tree.ActiveRecordingId;
            if (string.IsNullOrEmpty(recId))
            {
                ParsekLog.Warn("Flight",
                    "FlushRecorderToTreeRecording: dropping recorder data because active recording id is missing; " +
                    FormatRecorderDropDiagnostics(rec, tree, recId));
                rec.IsRecording = false;
                return;
            }

            Recording treeRec;
            if (!tree.Recordings.TryGetValue(recId, out treeRec))
            {
                ParsekLog.Warn("Flight",
                    $"FlushRecorderToTreeRecording: dropping recorder data because recording id '{recId}' " +
                    $"is not found in tree; {FormatRecorderDropDiagnostics(rec, tree, recId)}");
                rec.IsRecording = false;
                return;
            }

            // Close any open orbit segment (e.g. background phase) before copying data
            rec.FinalizeOpenOrbitSegment();

            // Append trajectory data
            treeRec.Points.AddRange(rec.Recording);
            treeRec.OrbitSegments.AddRange(rec.OrbitSegments);
            treeRec.PartEvents.AddRange(rec.PartEvents);
            treeRec.FlagEvents.AddRange(rec.FlagEvents);
            treeRec.SegmentEvents.AddRange(rec.SegmentEvents);
            treeRec.TrackSections.AddRange(rec.TrackSections);

            // Sort part/flag/segment events chronologically. PartEvents MUST use a STABLE sort so
            // same-UT events retain their insertion order — critical for terminal-vs-ignited
            // ordering across tree promotion boundaries (#287). FlagEvents and SegmentEvents use
            // the same stable pattern for consistency across every flush/merge site.
            var sortedPartEventsFlush = FlightRecorder.StableSortPartEventsByUT(treeRec.PartEvents);
            treeRec.PartEvents.Clear();
            treeRec.PartEvents.AddRange(sortedPartEventsFlush);
            var sortedFlagEventsFlush = FlightRecorder.StableSortByUT(treeRec.FlagEvents, e => e.ut);
            treeRec.FlagEvents.Clear();
            treeRec.FlagEvents.AddRange(sortedFlagEventsFlush);
            var sortedSegEventsFlush = FlightRecorder.StableSortByUT(treeRec.SegmentEvents, e => e.ut);
            treeRec.SegmentEvents.Clear();
            treeRec.SegmentEvents.AddRange(sortedSegEventsFlush);

            // Mark dirty so the next OnSave persists the flushed data to disk.
            // Without this, the in-memory points are lost on scene reload.
            treeRec.MarkFilesDirty();

            // Populate VesselPersistentId (required for RebuildBackgroundMap after save/load)
            if (treeRec.VesselPersistentId == 0 && rec.RecordingVesselId != 0)
                treeRec.VesselPersistentId = rec.RecordingVesselId;

            // Copy Phase 10 location fields if not already set on the tree recording
            if (string.IsNullOrEmpty(treeRec.StartBodyName))
                treeRec.StartBodyName = rec.StartBodyName;
            if (string.IsNullOrEmpty(treeRec.StartBiome))
                treeRec.StartBiome = rec.StartBiome;
            if (string.IsNullOrEmpty(treeRec.StartSituation))
                treeRec.StartSituation = rec.StartSituation;
            if (string.IsNullOrEmpty(treeRec.LaunchSiteName))
                treeRec.LaunchSiteName = rec.LaunchSiteName;

            // Forward start-of-recording controller identity onto the tree recording
            // if it does not already carry it. Critical for the BG go-on-rails
            // identity-loss override: if the always-tree root was created without
            // a live vessel and the recorder.StartRecording backstop did not fire
            // (e.g. the tree ActiveRecordingId was not yet pointed at this rec at
            // the time), this flush is the final chance to persist the identity
            // before the recording can be backgrounded.
            if (treeRec.AdoptControllersIfEmpty(rec.StartControllers))
            {
                ParsekLog.Verbose("Flight",
                    $"FlushRecorderToTreeRecording: forwarded {treeRec.Controllers.Count} controller part(s) " +
                    $"from recorder to tree recording '{recId}'");
            }

            ApplyCapturedLogisticsMetadataToRecording(
                treeRec,
                rec.CaptureAtStop,
                "FlushRecorderToTreeRecording");

            ApplyFinalSegmentPhaseFromCapture(
                treeRec,
                tree.ActiveRecordingId,
                rec.CaptureAtStop,
                tree);

            // Set IsRecording = false to prevent dangling active state
            rec.IsRecording = false;
            rec.TransitionToBackgroundPending = false;

            ParsekLog.Info("Flight", $"Flushed recorder to tree recording '{recId}': " +
                $"{rec.Recording.Count} points, {rec.OrbitSegments.Count} orbit segments, " +
                $"{rec.PartEvents.Count} part events");
        }

        internal static string FormatRecorderDropDiagnostics(FlightRecorder rec, RecordingTree tree, string attemptedRecordingId)
        {
            return string.Format(CultureInfo.InvariantCulture,
                "tree={0} activeRec={1} attemptedRec={2} recVesselPid={3} " +
                "points={4} orbitSegments={5} partEvents={6} flagEvents={7} segmentEvents={8} trackSections={9}",
                string.IsNullOrEmpty(tree?.Id) ? "null" : tree.Id,
                string.IsNullOrEmpty(tree?.ActiveRecordingId) ? "null" : tree.ActiveRecordingId,
                string.IsNullOrEmpty(attemptedRecordingId) ? "null" : attemptedRecordingId,
                rec?.RecordingVesselId ?? 0u,
                rec?.Recording?.Count ?? 0,
                rec?.OrbitSegments?.Count ?? 0,
                rec?.PartEvents?.Count ?? 0,
                rec?.FlagEvents?.Count ?? 0,
                rec?.SegmentEvents?.Count ?? 0,
                rec?.TrackSections?.Count ?? 0);
        }

        /// <summary>
        /// Creates a new FlightRecorder for the promoted vessel, starts recording with
        /// isPromotion=true, updates activeTree state.
        /// </summary>
        internal void PromoteRecordingFromBackground(
            string backgroundRecordingId,
            Vessel newVessel)
        {
            if (activeTree == null || newVessel == null) return;

            RecorderStateLog.RecState("PromoteFromBackground:entry", CaptureRecorderState());

            // Callers pass the target recording id explicitly; BackgroundMap membership is
            // not a hard precondition here. The committed-tree restore path may already have
            // removed stale entries for this recording before the live promotion runs.
            activeTree.BackgroundMap.Remove(newVessel.persistentId);

            // Set as the active recording
            activeTree.ActiveRecordingId = backgroundRecordingId;

            // Create a new recorder and start recording with isPromotion
            recorder = new FlightRecorder();
            recorder.ActiveTree = activeTree;
            recorder.StartRecording(isPromotion: true);

            if (!recorder.IsRecording)
            {
                ParsekLog.Warn("Flight", $"PromoteRecordingFromBackground: StartRecording failed for pid={newVessel.persistentId}");
                recorder = null;
                activeTree.ActiveRecordingId = null;
                // Put it back in the background map
                activeTree.BackgroundMap[newVessel.persistentId] = backgroundRecordingId;
                backgroundRecorder?.OnVesselBackgrounded(newVessel.persistentId);
                return;
            }

            PrepareSessionStateForRecorderStart("PromoteRecordingFromBackground");

            // BUG #2: same live-resume anchor capture as ResumeCommittedActiveRecording —
            // a promoted background recording can also carry an old StartUT, so anchor on
            // the UT at which this recorder session began appending.
            liveResumeSessionStartUT = SafeCurrentUtForResumeAnchor();
            ParsekLog.Verbose("Flight",
                $"liveResumeSessionStartUT set reason=PromoteRecordingFromBackground " +
                $"recId={backgroundRecordingId} " +
                $"ut={liveResumeSessionStartUT.ToString("R", CultureInfo.InvariantCulture)}");

            ParsekLog.Info("Flight", $"Promoted recording '{backgroundRecordingId}' from background " +
                $"(pid={newVessel.persistentId})");
            RecorderStateLog.RecState("PromoteFromBackground:exit", CaptureRecorderState());
        }

        /// <summary>
        /// Captures the active vessel's pose after every physics step while a foreground
        /// recorder runs, so a physics callback that fires inside FixedUpdate (before that
        /// step's PhysX simulate) can still sample a UT and position from one step. See
        /// <see cref="PostPhysicsPoseCache"/>.
        /// </summary>
        private IEnumerator PostPhysicsPoseCaptureLoop()
        {
            var waitForFixedUpdate = new WaitForFixedUpdate();
            ParsekLog.Verbose("Recorder", "Post-physics pose capture loop started");
            while (true)
            {
                yield return waitForFixedUpdate;
                if (Patches.PhysicsFramePatch.ActiveRecorder == null)
                {
                    PostPhysicsPoseCache.Clear();
                    continue;
                }

                try
                {
                    PostPhysicsPoseCache.Capture(FlightGlobals.ActiveVessel);
                }
                catch (Exception ex)
                {
                    PostPhysicsPoseCache.Clear();
                    ParsekLog.WarnRateLimited("Recorder", "post-physics-pose-capture-failed",
                        $"Post-physics pose capture failed: {ex.GetType().Name}: {ex.Message}");
                }
            }
        }

        private void EnsureBackgroundRecorderAttached(string reason)
        {
            if (activeTree == null || backgroundRecorder != null)
                return;

            backgroundRecorder = new BackgroundRecorder(activeTree);
            backgroundRecorder.SubscribePartEvents();
            Patches.PhysicsFramePatch.BackgroundRecorderInstance = backgroundRecorder;
            ParsekLog.Verbose("Flight",
                $"{reason}: attached BackgroundRecorder for tree '{activeTree.TreeName}' " +
                $"({activeTree.BackgroundMap.Count} background entry(ies))");

            // Callers attach either to a fresh tree (empty map, nothing to check) or to one
            // restored from a save or the committed list, so this is the load-time point at
            // which a background member stock dropped from the save (vessel budget / KSC
            // declutter) is first seen without a vessel. It is closed here, before the first
            // UpdateOnRails tick could advance its end UT past the save.
            HashSet<uint> livePids = TryBuildLiveVesselPidSet();
            if (livePids != null && activeTree.BackgroundMap.Count > 0)
                backgroundRecorder.CloseMissingBackgroundMembersAtLoad(livePids.Contains, reason);
        }

        void OnVesselSituationChange(GameEvents.HostedFromToAction<Vessel, Vessel.Situations> data)
        {
            if (data.host != null && GhostMapPresence.IsGhostMapVessel(data.host.persistentId)) return;
            double currentUT = Planetarium.GetUniversalTime();
            bool suppressLaunchAutoRecordForTimeJump =
                TimeJumpManager.IsTimeJumpLaunchAutoRecordSuppressed(
                    TimeJumpManager.IsTimeJumpLaunchAutoRecordInProgress,
                    Time.frameCount,
                    TimeJumpManager.TimeJumpLaunchAutoRecordSuppressUntilFrame);

            // Track when the active vessel enters LANDED/SPLASHED for the settle timer
            if (data.host == FlightGlobals.ActiveVessel &&
                (data.to == Vessel.Situations.LANDED || data.to == Vessel.Situations.SPLASHED))
            {
                lastLandedUT = currentUT;
            }

            var launchDecision = ParsekFlightPostSwitchAutoRecord.EvaluateAutoRecordLaunchDecision(
                isRecording: IsRecording,
                isActiveVessel: data.host == FlightGlobals.ActiveVessel,
                fromSituation: data.from,
                autoRecordOnLaunchEnabled: ParsekSettings.Current?.autoRecordOnLaunch != false,
                lastLandedUt: lastLandedUT,
                currentUt: currentUT,
                landedSettleThreshold: LandedSettleThreshold,
                suppressForTimeJumpTransient: suppressLaunchAutoRecordForTimeJump);

            switch (launchDecision)
            {
                case AutoRecordLaunchDecision.SkipAlreadyRecording:
                    return;

                case AutoRecordLaunchDecision.SkipInactiveVessel:
                    ParsekLog.VerboseRateLimited("Flight", "sit-change-other",
                        $"OnVesselSituationChange: ignoring non-active vessel " +
                        $"'{data.host?.vesselName ?? "null"}' pid={data.host?.persistentId ?? 0u} " +
                        $"({data.from} → {data.to})");
                    return;

                case AutoRecordLaunchDecision.SkipBounce:
                    double settledTime = lastLandedUT >= 0
                        ? currentUT - lastLandedUT
                        : 0;
                    ParsekLog.VerboseRateLimited("Flight", "sit-change-bounce",
                        $"OnVesselSituationChange: LANDED → {data.to} after {settledTime:F1}s (< {LandedSettleThreshold}s settle threshold)");
                    return;

                case AutoRecordLaunchDecision.SkipNotLaunchTransition:
                    ParsekLog.VerboseRateLimited("Flight", "sit-change-not-launch",
                        $"OnVesselSituationChange: not a launch transition ({data.from} → {data.to})");
                    return;

                case AutoRecordLaunchDecision.SkipDisabled:
                    ParsekLog.Verbose("Flight", "OnVesselSituationChange: auto-record disabled in settings");
                    return;

                case AutoRecordLaunchDecision.SkipTimeJumpTransient:
                    ParsekLog.Info("Flight",
                        $"OnVesselSituationChange: suppressing time-jump transient ({data.from} → {data.to}) " +
                        $"for '{data.host?.vesselName ?? "null"}' frame={Time.frameCount} " +
                        $"suppressUntilFrame={TimeJumpManager.TimeJumpLaunchAutoRecordSuppressUntilFrame}");
                    return;
            }

            StartRecording(suppressStartScreenMessage: true,
                fromPrelaunchTransition: launchDecision == AutoRecordLaunchDecision.StartFromPrelaunch);
            Log($"Auto-record started ({data.from} → {data.to})");
            ScreenMessage("Recording STARTED (auto)", 2f);
        }

        // First-staging-on-pad auto-record trigger. A stage activated while the active
        // vessel is still PRELAUNCH (e.g. igniting SRBs that then decouple and fly off
        // the pad before the main stage lifts off) must start the recorder NOW so the
        // subsequent decouple is captured as a debris branch of the recorded vessel.
        // Without this the recorder only starts on the main stage's PRELAUNCH->FLYING
        // transition, by which point the separated debris is already a foreign vessel
        // and is dropped by the decouple capture gate.
        void OnStageActivate(int stage)
        {
            Vessel av = FlightGlobals.ActiveVessel;
            if (av != null && GhostMapPresence.IsGhostMapVessel(av.persistentId)) return;

            bool suppressLaunchAutoRecordForTimeJump =
                TimeJumpManager.IsTimeJumpLaunchAutoRecordSuppressed(
                    TimeJumpManager.IsTimeJumpLaunchAutoRecordInProgress,
                    Time.frameCount,
                    TimeJumpManager.TimeJumpLaunchAutoRecordSuppressUntilFrame);

            var stagingDecision = ParsekFlightPostSwitchAutoRecord.EvaluateAutoRecordStagingDecision(
                isRecording: IsRecording,
                hasActiveVessel: av != null,
                activeVesselIsPrelaunch: av != null && av.situation == Vessel.Situations.PRELAUNCH,
                autoRecordOnLaunchEnabled: ParsekSettings.Current?.autoRecordOnLaunch != false,
                suppressForTimeJumpTransient: suppressLaunchAutoRecordForTimeJump);

            switch (stagingDecision)
            {
                case AutoRecordStagingDecision.SkipAlreadyRecording:
                    return;

                case AutoRecordStagingDecision.SkipTimeJumpTransient:
                    ParsekLog.Info("Flight",
                        $"OnStageActivate: suppressing time-jump transient (stage={stage}) " +
                        $"for '{av?.vesselName ?? "null"}' frame={Time.frameCount} " +
                        $"suppressUntilFrame={TimeJumpManager.TimeJumpLaunchAutoRecordSuppressUntilFrame}");
                    return;

                case AutoRecordStagingDecision.SkipNoActiveVessel:
                    ParsekLog.VerboseRateLimited("Flight", "stage-no-active",
                        $"OnStageActivate: no active vessel (stage={stage})");
                    return;

                case AutoRecordStagingDecision.SkipNotOnPad:
                    ParsekLog.VerboseRateLimited("Flight", "stage-not-prelaunch",
                        $"OnStageActivate: active vessel '{av?.vesselName ?? "null"}' not PRELAUNCH " +
                        $"(stage={stage}, situation={av?.situation}): in-flight staging left to recorder/launch trigger");
                    return;

                case AutoRecordStagingDecision.SkipDisabled:
                    ParsekLog.Verbose("Flight", "OnStageActivate: auto-record disabled in settings");
                    return;
            }

            StartRecording(suppressStartScreenMessage: true);
            Log($"Auto-record started (first staging on pad, stage={stage})");
            ScreenMessage("Recording STARTED (auto)", 2f);
        }

        void OnVesselWasModified(Vessel v)
        {
            if (v == null || GhostMapPresence.IsGhostMapVessel(v.persistentId))
                return;

            // M5: the recorder's engine/RCS/robotic caches are built once at StartRecording and
            // hold direct Part/PartModule references. Rebuild them here so a staged-away booster
            // stops being polled into this recording and a welded-on / newly docked module starts
            // being polled at all. No-ops unless v IS the recorded vessel.
            recorder?.OnVesselWasModified(v);

            PostSwitchAutoRecordState state = postSwitchAutoRecord;
            if (state == null || state.VesselPid != v.persistentId)
                return;

            state.ModuleCachesDirty = true;
            state.NextManifestEvaluationUt = PostSwitchManifestEvaluateNextFrameUt;
            ParsekLog.VerboseRateLimited("Flight", "post-switch-auto-record-invalidated",
                $"Post-switch watch invalidated module caches: pid={state.VesselPid} " +
                $"vessel='{state.VesselName}'", 1.0);
        }

        void OnVesselGoOnRails(Vessel v)
        {
            if (v != null && GhostMapPresence.IsGhostMapVessel(v.persistentId)) return;
            // Track pre-pack vessel state for phantom terrain crash detection
            if (v != null)
                packStates[v.persistentId] = (Planetarium.GetUniversalTime(), v.situation);
            recorder?.OnVesselGoOnRails(v);
            backgroundRecorder?.OnBackgroundVesselGoOnRails(v);
        }

        void OnVesselGoOffRails(Vessel v)
        {
            if (v != null && GhostMapPresence.IsGhostMapVessel(v.persistentId)) return;
            if (v != null) packStates.Remove(v.persistentId);
            recorder?.OnVesselGoOffRails(v);
            backgroundRecorder?.OnBackgroundVesselGoOffRails(v);
        }

        void OnVesselLoaded(Vessel v)
        {
            if (v == null) return;
            if (GhostMapPresence.IsGhostMapVessel(v.persistentId)) return;

            uint pid = v.persistentId;
            loadedAnchorVessels.Add(pid);

            var committed = RecordingStore.CommittedRecordings;
            for (int i = 0; i < committed.Count; i++)
            {
                var rec = committed[i];
                if (rec.LoopAnchorVesselId != pid || !rec.LoopPlayback)
                    continue;

                string anchorBody = v.mainBody?.name;
                if (!GhostPlaybackLogic.ShouldSpawnLoopedGhost(rec, true, anchorBody, rec.LoopAnchorBodyName))
                    continue;

                double currentUT = Planetarium.GetUniversalTime();
                if (TryComputeLoopPlaybackUT(rec, currentUT,
                        out double loopUT, out long cycleIndex, out bool isInPause, i))
                {
                    ParsekLog.Info("Loop",
                        $"Anchor vessel loaded: pid={pid}, rec #{i} '{rec.VesselName}' " +
                        $"phase cycle={cycleIndex} loopUT={loopUT:F2} paused={isInPause}");
                }
                else if (TryGetLoopSchedule(
                             rec, i,
                             out _,
                             out double scheduleStartUT,
                             out _,
                             out double intervalSeconds))
                {
                    ParsekLog.Info("Loop",
                        $"Anchor vessel loaded: pid={pid}, rec #{i} '{rec.VesselName}' " +
                        $"queued launch pending scheduleStartUT={scheduleStartUT:F2} interval={intervalSeconds:F2}");
                }

                // The loop playback system in GhostPlaybackEngine.UpdateLoopingPlayback
                // will pick up this recording on the next Update frame and spawn the
                // ghost at the computed phase — no direct spawn here avoids duplicate spawning.
            }
        }

        /// <summary>
        /// Pure: adds every already-loaded vessel pid to the loaded-anchor set, skipping ghost
        /// map vessels the same way <see cref="OnVesselLoaded"/> does. The vessel being flown
        /// is loaded by <c>FlightDriver.Start</c> before this scene subscribes to
        /// <c>onVesselLoaded</c>, so without the seed a loop anchored to it never plays
        /// (LOOP-ANCHOR-ACTIVE-VESSEL-NEVER-MARKED-LOADED). Returns how many pids were added.
        /// </summary>
        internal static int SeedLoadedAnchorVessels(
            HashSet<uint> loadedAnchors, IList<uint> loadedVesselPids,
            Func<uint, bool> isGhostMapVessel, out int ghostSkipped)
        {
            ghostSkipped = 0;
            if (loadedAnchors == null || loadedVesselPids == null)
                return 0;
            int added = 0;
            for (int i = 0; i < loadedVesselPids.Count; i++)
            {
                uint pid = loadedVesselPids[i];
                if (pid == 0)
                    continue;
                if (isGhostMapVessel != null && isGhostMapVessel(pid))
                {
                    ghostSkipped++;
                    continue;
                }
                if (loadedAnchors.Add(pid))
                    added++;
            }
            return added;
        }

        private void SeedLoadedAnchorVesselsFromScene(string reason)
        {
            if (engine == null)
                return;
            List<uint> pids = ReadLoadedVesselPids();
            int added = SeedLoadedAnchorVessels(
                loadedAnchorVessels, pids, GhostMapPresence.IsGhostMapVessel, out int ghostSkipped);
            ParsekLog.Verbose("Loop",
                $"Loaded-anchor set seeded: reason={reason} loaded={pids.Count} added={added} " +
                $"ghostSkipped={ghostSkipped} total={loadedAnchorVessels.Count}");
        }

        [System.Runtime.CompilerServices.MethodImpl(
            System.Runtime.CompilerServices.MethodImplOptions.NoInlining)]
        private static List<uint> ReadLoadedVesselPids()
        {
            var pids = new List<uint>();
            var loaded = FlightGlobals.VesselsLoaded;
            if (loaded == null)
                return pids;
            for (int i = 0; i < loaded.Count; i++)
            {
                Vessel v = loaded[i];
                if (v != null)
                    pids.Add(v.persistentId);
            }
            return pids;
        }

        void OnVesselUnloaded(Vessel v)
        {
            if (v == null) return;
            if (GhostMapPresence.IsGhostMapVessel(v.persistentId)) return;
            uint pid = v.persistentId;
            loadedAnchorVessels.Remove(pid);

            var committed = RecordingStore.CommittedRecordings;
            for (int i = 0; i < committed.Count; i++)
            {
                var rec = committed[i];
                if (rec.LoopAnchorVesselId != pid || !rec.LoopPlayback)
                    continue;

                if (ghostStates.ContainsKey(i))
                {
                    ParsekLog.Info("Loop",
                        $"Anchor vessel unloaded: pid={pid}, destroying looped ghost #{i} '{rec.VesselName}'");
                    DestroyAllOverlapGhosts(i);
                    engine.DestroyGhost(i);
                }
            }
        }

        void OnVesselSOIChanged(GameEvents.HostedFromToAction<Vessel, CelestialBody> data)
        {
            if (data.host != null && GhostMapPresence.IsGhostMapVessel(data.host.persistentId)) return;

            recorder?.OnVesselSOIChanged(data);
            backgroundRecorder?.OnBackgroundVesselSOIChanged(data.host, data.from);
        }

        /// <summary>
        /// Stock Alt+F12 teleport (Set Orbit / Rendezvous / Set Position) finished
        /// <c>FlightGlobals.PostOrbitSet</c> for the active vessel; forwarded by
        /// <see cref="Patches.CheatTeleportPatch"/>. Returns false when no recorder exists,
        /// so the patch writes the log line itself.
        /// </summary>
        internal bool HandleCheatTeleport(Vessel v, CelestialBody oldBody)
        {
            if (v != null && GhostMapPresence.IsGhostMapVessel(v.persistentId)) return true;
            if (recorder == null)
                return false;
            recorder.OnCheatTeleport(v, oldBody);
            return true;
        }

        void OnTimeWarpRateChanged()
        {
            bool isWarpNow = IsAnyWarpActive();

            // Detect warp start: was not warping, now warping — patch facility visuals
            // so the player sees the correct state during warp. Full recalculation
            // happens on warp exit; this is a lightweight visual-only update.
            // NOTE: True real-time per-frame facility visual updates during warp would
            // require a partial recalculation walk (processing actions only up to the
            // current UT). The current approach patches to the final derived state at
            // warp start and again at warp end, which covers the common case of warping
            // past a facility event.
            if (!wasWarpActive && isWarpNow)
            {
                warpStartUT = Planetarium.GetUniversalTime();
                if (LedgerOrchestrator.IsInitialized && !Ledger.IsHeldFromNewerVersion)
                {
                    // Same suppression PatchAll wraps it in: the patch's SetLevel /
                    // Demolish / Repair fire the stock facility and building events, which
                    // must not read back as player upgrades, collapses or repairs.
                    using (SuppressionGuard.ResourcesAndReplay())
                        KspStatePatcher.PatchFacilities(LedgerOrchestrator.Facilities);
                    ParsekLog.Info("WarpFacilities",
                        "Warp start at UT="
                        + warpStartUT.ToString("R", CultureInfo.InvariantCulture)
                        + " — patched facility visuals");
                }
                else if (Ledger.IsHeldFromNewerVersion)
                {
                    ParsekLog.Verbose("WarpFacilities",
                        "Warp start: facility patch skipped (ledger held from a newer Parsek version)");
                }
            }

            // Detect warp exit: was warping, now at 1x — recalculate ledger
            if (wasWarpActive && !isWarpNow)
            {
                // Snapshot the handler-fire UT. Exact alignment with KSP's internal
                // warp boundary is out of scope for this cutoff seam.
                double warpEndUT = Planetarium.GetUniversalTime();
                RecalculateLedgerAfterWarpExit(warpEndUT);

                // Log whether any facility actions were crossed during this warp session
                if (LedgerOrchestrator.IsInitialized &&
                    LedgerOrchestrator.HasFacilityActionsInRange(warpStartUT, warpEndUT))
                {
                    ParsekLog.Info("WarpFacilities",
                        "Warp exit at UT="
                        + warpEndUT.ToString("R", CultureInfo.InvariantCulture)
                        + " — facility actions crossed in range ("
                        + warpStartUT.ToString("R", CultureInfo.InvariantCulture)
                        + ", "
                        + warpEndUT.ToString("R", CultureInfo.InvariantCulture)
                        + "]");
                }
            }
            wasWarpActive = isWarpNow;

            if (activeTree == null || backgroundRecorder == null) return;

            double ut = Planetarium.GetUniversalTime();
            float warpRate = TimeWarp.CurrentRate;
            string warpRateKey = warpRate.ToString("F1",
                System.Globalization.CultureInfo.InvariantCulture);

            bool sameCheckpointTree = ReferenceEquals(lastWarpCheckpointTree, activeTree);
            if (ShouldSkipDuplicateWarpCheckpointEvent(
                    warpRate,
                    ut,
                    hasLastWarpCheckpointEvent && sameCheckpointTree,
                    lastWarpCheckpointRate,
                    lastWarpCheckpointUT))
            {
                ParsekLog.VerboseRateLimited("Checkpoint",
                    $"warp-rate-duplicate-{warpRateKey}",
                    $"Duplicate time warp checkpoint ignored at {warpRateKey}x UT={ut:F2}");
                return;
            }

            hasLastWarpCheckpointEvent = true;
            lastWarpCheckpointTree = activeTree;
            lastWarpCheckpointRate = warpRate;
            lastWarpCheckpointUT = ut;

            // Bug #592: KSP fires onTimeWarpRateChanged very chattily — a single
            // playtest produced ~1090 events at 1.0x without any actual rate change
            // (scene transitions, warp-to-here, etc. retrigger the GameEvent).
            // Rate-limit by warpRate so transitions between distinct rates still log
            // immediately, but a burst of redundant 1x->1x fires collapses into one
            // line per window. Bug #597 additionally skips exact duplicate
            // same-tree/same-rate/same-UT checkpoint work before it reaches the recorder.
            ParsekLog.VerboseRateLimited("Checkpoint",
                $"warp-rate-changed-{warpRateKey}",
                $"Time warp rate changed to {warpRateKey}x " +
                $"at UT={ut:F2} — checkpointing all background vessels");

            // Checkpoint background vessels first (before any state changes)
            backgroundRecorder.CheckpointAllVessels(ut);

            // The active vessel's orbit segments are already handled by
            // onVesselGoOnRails/onVesselGoOffRails events which fire when
            // time warp transitions between physics and rails modes.
            ParsekLog.VerboseRateLimited("Checkpoint",
                $"warp-rate-changed-on-rails-{warpRateKey}",
                "Active vessel orbit segments handled by on-rails events");
        }

        internal static void RecalculateLedgerAfterWarpExit(double warpEndUT)
        {
            RecalculateLedgerAfterWarpExit(
                warpEndUT,
                LedgerOrchestrator.RecalculateAndPatchForTimeJump);
        }

        internal static void RecalculateLedgerAfterWarpExit(
            double warpEndUT,
            Action<double> recalculateAndPatchForTimeJump)
        {
            if (recalculateAndPatchForTimeJump == null)
                throw new ArgumentNullException(nameof(recalculateAndPatchForTimeJump));

            try
            {
                ParsekLog.Info("LedgerOrchestrator",
                    "Warp exit ledger recalculation started: cutoffUT="
                    + warpEndUT.ToString("R", CultureInfo.InvariantCulture));
                recalculateAndPatchForTimeJump(warpEndUT);
                ParsekLog.Info("LedgerOrchestrator",
                    "Warp exit ledger recalculation complete: cutoffUT="
                    + warpEndUT.ToString("R", CultureInfo.InvariantCulture));
            }
            catch (Exception ex)
            {
                ParsekLog.Warn("LedgerOrchestrator",
                    "Warp exit ledger recalculation failed: cutoffUT="
                    + warpEndUT.ToString("R", CultureInfo.InvariantCulture)
                    + " error="
                    + ex.GetType().Name
                    + ": "
                    + ex.Message);
            }
        }

        internal static bool ShouldSkipDuplicateWarpCheckpointEvent(
            float currentWarpRate,
            double currentUT,
            bool hasLastEvent,
            float lastWarpRate,
            double lastUT)
        {
            if (!hasLastEvent)
                return false;

            if (float.IsNaN(currentWarpRate) || float.IsInfinity(currentWarpRate) ||
                double.IsNaN(currentUT) || double.IsInfinity(currentUT) ||
                float.IsNaN(lastWarpRate) || float.IsInfinity(lastWarpRate) ||
                double.IsNaN(lastUT) || double.IsInfinity(lastUT))
                return false;

            const double RateEpsilon = 0.0001;
            const double UTEpsilon = 0.000001;
            return Math.Abs((double)currentWarpRate - lastWarpRate) <= RateEpsilon
                && Math.Abs(currentUT - lastUT) <= UTEpsilon;
        }

        // Captures the scene-entry vessel pid for the fresh-launch restore guard.
        // Runs once during Start; the captured pid is then the only one the guard
        // rejects, so vessel switches to other already-spawned committed vessels
        // in the same scene keep resuming their recordings.
        private void CaptureFreshRolloutVesselPidIfApplicable()
        {
            // Default to 0 every scene so a non-fresh startup (resumed save / revert)
            // leaves the shared static cleared and the crew-swap call sites keep
            // running orphan placement. The instance field defaults to 0 per new
            // ParsekFlight; the static must be reset explicitly.
            freshRolloutVesselPid = 0;
            SceneEntryVesselScope.SceneEntryFreshRolloutVesselPid = 0;

            FlightDriver.StartupBehaviours startup = FlightDriver.StartupBehaviour;
            if (!IsFreshLaunchStartupBehaviour(startup))
                return;
            Vessel v = FlightGlobals.ActiveVessel;
            if (v == null)
            {
                ParsekLog.Warn("Flight",
                    $"FreshRollout: cannot capture scene-entry vessel pid — " +
                    $"FlightGlobals.ActiveVessel is null at Start (StartupBehaviour={startup}); " +
                    "fresh-launch restore guard is inactive for this scene");
                return;
            }
            freshRolloutVesselPid = v.persistentId;
            // Publish to the shared static so SwapReservedCrewInFlight can suppress
            // Pass-2 orphan placement for this fresh launch from any call site
            // (flight-ready, chain-commit, tree-commit) without a ParsekFlight handle.
            SceneEntryVesselScope.SceneEntryFreshRolloutVesselPid = freshRolloutVesselPid;
            ParsekLog.Info("Flight",
                $"FreshRollout: captured scene-entry vessel pid={freshRolloutVesselPid} " +
                $"('{v.vesselName}', StartupBehaviour={startup}) — committed-tree restore " +
                "will skip only this pid for the lifetime of this scene");
        }

        /// <summary>
        /// Returns true if the current recording mode suppresses environment boundary
        /// splits. Tree mode does, and every recording runs in a tree: the recorder keeps
        /// one recording across the boundary and the optimizer splits it afterwards.
        /// </summary>
        internal static bool ShouldSuppressBoundarySplit(RecordingTree activeTree)
        {
            return activeTree != null;
        }

        /// <summary>
        /// Handles atmosphere boundary auto-split when crossing the atmosphere edge.
        /// In tree mode the recording continues across the edge and only the flags clear.
        /// </summary>
        private void HandleAtmosphereBoundarySplit()
        {
            if (recorder == null || !recorder.IsRecording || !recorder.AtmosphereBoundaryCrossed)
                return;

            // Bug #87: In tree mode, boundary splits must not commit standalone chain segments.
            // The recorder keeps accumulating data into the tree recording — just clear the
            // boundary flags so they don't re-trigger.
            if (ShouldSuppressBoundarySplit(activeTree))
            {
                string skipPhase = recorder.EnteredAtmosphere ? "atmo" : "exo";
                string skipBody = FlightGlobals.ActiveVessel?.mainBody?.name ?? "Unknown";
                ParsekLog.Info("Flight", $"Atmosphere boundary suppressed in tree mode: " +
                    $"{skipBody} entering {skipPhase} (tree={activeTree.Id}, " +
                    $"activeRec={activeTree.ActiveRecordingId})");
                recorder.ClearBoundaryFlags();
                return;
            }

            ParsekLog.Warn("Flight", "Atmosphere boundary crossed with no active tree - clearing boundary flags");
            recorder.ClearBoundaryFlags();
        }

        /// <summary>
        /// Handles SOI change auto-split when transitioning between celestial bodies.
        /// In tree mode the recording continues across the SOI change and only the flags clear.
        /// </summary>
        private void HandleSoiChangeSplit()
        {
            if (recorder == null || !recorder.IsRecording || !recorder.SoiChangePending)
                return;

            // Bug #87: In tree mode, boundary splits must not commit standalone chain segments.
            // The recorder keeps accumulating data into the tree recording — just clear the
            // boundary flags so they don't re-trigger.
            if (ShouldSuppressBoundarySplit(activeTree))
            {
                string skipFrom = recorder.SoiChangeFromBody ?? "Unknown";
                string skipTo = FlightGlobals.ActiveVessel?.mainBody?.name ?? "Unknown";
                ParsekLog.Info("Flight", $"SOI change boundary suppressed in tree mode: " +
                    $"{skipFrom} to {skipTo} (tree={activeTree.Id}, " +
                    $"activeRec={activeTree.ActiveRecordingId})");
                recorder.ClearBoundaryFlags();
                return;
            }

            ParsekLog.Warn("Flight", "SOI change with no active tree - clearing boundary flags");
            recorder.ClearBoundaryFlags();
        }

        /// <summary>
        /// Handles altitude boundary auto-split when crossing the approach altitude threshold
        /// on an airless body. Mirrors HandleAtmosphereBoundarySplit.
        /// </summary>
        private void HandleAltitudeBoundarySplit()
        {
            if (recorder == null || !recorder.IsRecording || !recorder.AltitudeBoundaryCrossed)
                return;

            // Bug #87: In tree mode, boundary splits must not commit standalone chain segments.
            if (ShouldSuppressBoundarySplit(activeTree))
            {
                string skipPhase = recorder.DescendedBelowThreshold ? "approach" : "exo";
                string skipBody = FlightGlobals.ActiveVessel?.mainBody?.name ?? "Unknown";
                ParsekLog.Info("Flight", $"Altitude boundary suppressed in tree mode: " +
                    $"{skipBody} entering {skipPhase} (tree={activeTree.Id}, " +
                    $"activeRec={activeTree.ActiveRecordingId})");
                recorder.ClearBoundaryFlags();
                return;
            }

            ParsekLog.Warn("Flight", "Altitude boundary crossed with no active tree - clearing boundary flags");
            recorder.ClearBoundaryFlags();
        }

        /// <summary>
        /// Flushes the backgrounded recorder to its tree recording and updates BackgroundMap.
        /// Called from Update when TransitionToBackgroundPending and IsBackgrounded are both true.
        /// </summary>
        private void HandleTreeBackgroundFlush()
        {
            if (activeTree == null || recorder == null || !recorder.TransitionToBackgroundPending
                || !recorder.IsBackgrounded)
                return;

            EmitPendingSplitBeforeFocusLeaves("HandleTreeBackgroundFlush");
            FlushRecorderToTreeRecording(recorder, activeTree);

            // Add old vessel to BackgroundMap
            string oldRecId = activeTree.ActiveRecordingId;
            Recording oldRec = null;
            uint bgPidUpdate = 0;
            if (oldRecId != null && activeTree.Recordings.TryGetValue(oldRecId, out oldRec)
                && oldRec.VesselPersistentId != 0)
            {
                activeTree.BackgroundMap[oldRec.VesselPersistentId] = oldRecId;
                bgPidUpdate = oldRec.VesselPersistentId;
            }
            activeTree.ActiveRecordingId = null;
            RecordingFinalizationCache bgCacheUpdate = oldRec != null
                ? recorder.GetFinalizationCacheForRecording(oldRec)
                : recorder.FinalizationCache;
            recorder = null;
            if (bgPidUpdate != 0) backgroundRecorder?.OnVesselBackgrounded(
                bgPidUpdate,
                inheritedFinalizationCache: bgCacheUpdate);
            Log($"Tree: recorder flushed to background (recId={oldRecId})");
        }

        // Pass suppressStartScreenMessage=true for fresh starts where the caller posts
        // its own custom screen message.
        public void StartRecording(bool suppressStartScreenMessage = false,
            bool fromPrelaunchTransition = false)
        {
            // Commit orphaned CaptureAtStop from a previous recorder that was stopped
            // by vessel switch but never committed (e.g., auto-record started on new
            // vessel before scene change). Without this, the old recording data is lost.
            // A capture of a tree the scene already dropped is discarded, not committed
            // (TryDiscardCaptureOfDroppedTree).
            if (recorder != null && !recorder.IsRecording && recorder.CaptureAtStop != null
                && activeTree == null)
            {
                FallbackCommitSplitRecorder(recorder);
                ParsekLog.Info("Flight", "Resolved orphaned recorder capture before starting new one");
            }

            uint activePid = FlightGlobals.ActiveVessel?.persistentId ?? 0u;
            string invalidHeadReason;
            if (!CanStartRecorderWithActiveTreeHead(activeTree, activePid, out invalidHeadReason))
            {
                ParsekLog.WarnRateLimited(
                    "Flight",
                    BuildInvalidActiveTreeHeadRateLimitKey(
                        "start-recording",
                        activeTree,
                        activePid,
                        invalidHeadReason),
                    $"StartRecording: refused to bind recorder to invalid active tree head " +
                    $"(reason={invalidHeadReason}, activePid={activePid}, " +
                    $"tree={activeTree?.Id ?? "null"}, activeRec={activeTree?.ActiveRecordingId ?? "null"})");
                return;
            }

            recorder = new FlightRecorder();

            // Bug #271: always-tree mode. When no tree exists, wrap the new recording
            // in a single-node tree. This eliminates the standalone/tree dual-path
            // architecture — every recording is a tree recording, even single-vessel
            // flights.
            if (activeTree == null)
            {
                string treeId = Guid.NewGuid().ToString("N");
                string rootRecId = Guid.NewGuid().ToString("N");
                uint vesselPid = FlightGlobals.ActiveVessel?.persistentId ?? 0;

                activeTree = new RecordingTree
                {
                    Id = treeId,
                    TreeName = Recording.ResolveLocalizedName(
                        FlightGlobals.ActiveVessel?.vesselName) ?? "Unknown",
                    RootRecordingId = rootRecId,
                    ActiveRecordingId = rootRecId
                };

                // Phase F: PreTree* capture removed. The ledger is now the single
                // source of truth for funds/science/reputation; trees no longer
                // snapshot pre-launch career resources for lump-sum reconciliation.

                var rootRec = new Recording
                {
                    RecordingId = rootRecId,
                    TreeId = treeId,
                    VesselPersistentId = vesselPid,
                    VesselName = activeTree.TreeName,
                    RecordingFormatVersion = RecordingStore.CurrentRecordingFormatVersion,
                    // Pin start-of-recording controller identity directly on the
                    // always-tree root. Without this, a switch-away that backgrounds
                    // the root before recorder.StartRecording's tree-forward backstop
                    // runs would leave Controllers=null on the BG-tracked record and
                    // the identity-loss override at OnBackgroundVesselGoOnRails would
                    // never fire.
                    Controllers = ControllerInfo.CaptureFromVessel(FlightGlobals.ActiveVessel)
                };
                // Capture GhostVisualSnapshot at recording start — the FULL vessel
                // before any staging or breakup events. This is the snapshot used for
                // ghost mesh rendering. VesselSnapshot is captured later at stop/split
                // time and may reflect a reduced part count.
                if (FlightGlobals.ActiveVessel != null)
                {
                    var startSnap = FlightRecorder.TryBackupSnapshot(FlightGlobals.ActiveVessel);
                    if (startSnap != null)
                        rootRec.GhostVisualSnapshot = startSnap;
                }

                activeTree.AddOrReplaceRecording(rootRec);

                backgroundRecorder = new BackgroundRecorder(activeTree);
                backgroundRecorder.SubscribePartEvents();
                Patches.PhysicsFramePatch.BackgroundRecorderInstance = backgroundRecorder;

                ParsekLog.Info("Flight",
                    $"Always-tree: created single-node tree id={treeId}, root={rootRecId}, " +
                    $"vessel={activeTree.TreeName} (pid={vesselPid}), " +
                    $"ghostSnap={rootRec.GhostVisualSnapshot != null}");
            }

            // Propagate tree mode to new recorder so DecideOnVesselSwitch uses tree decisions
            if (activeTree != null)
                recorder.ActiveTree = activeTree;
            recorder.StartedFromPrelaunchTransition = fromPrelaunchTransition;
            recorder.StartRecording(
                isPromotion: false,
                suppressStartScreenMessage: suppressStartScreenMessage);
            if (!recorder.IsRecording)
            {
                ParsekLog.Warn("Flight", $"StartRecording blocked: {DetermineRecordingBlockReason()}");
                // Bug #271: clean up single-node tree created above if StartRecording failed
                if (activeTree != null && activeTree.Recordings.Count <= 1
                    && backgroundRecorder != null)
                {
                    backgroundRecorder.Shutdown();
                    Patches.PhysicsFramePatch.BackgroundRecorderInstance = null;
                    backgroundRecorder = null;
                    // The failed start drops this tree (the single-node tree created
                    // above, or a pre-existing one-recording tree) without stashing it as
                    // pending, so its armed committed-tree restore attempt must be
                    // cleared with it; the clear is id-matched and reads the id here,
                    // before the null.
                    CommittedTreeRestoreAttempt.ClearCommittedTreeRestoreAttemptForDroppedTree(
                        activeTree?.Id, "start-recording cleanup dropped the restore clone",
                        preserveIrreversibleTails: true);
                    activeTree = null;
                    ParsekLog.Info("Flight", "Cleaned up single-node tree after StartRecording failure");
                }
                return;
            }

            PrepareSessionStateForRecorderStart("StartRecording");

            uint pid = FlightGlobals.ActiveVessel != null ? FlightGlobals.ActiveVessel.persistentId : 0;
            ParsekLog.Info("Flight", $"StartRecording succeeded: pid={pid}, tree={activeTree != null}");
            RecorderStateLog.RecState("StartRecording:post", CaptureRecorderState());
        }

        private void PrepareSessionStateForRecorderStart(string reason)
        {
            PostSwitchAutoRecorder.DisarmPostSwitchAutoRecord(reason);

            // BUG #2: every recorder start resets the live-resume anchor to NaN; only
            // the no-session resume / promote paths re-arm it (after this call returns)
            // with the current UT. A fresh launch / split / switch-segment start leaves
            // it NaN, so the pre-switch dialog falls back to tree-wide (fresh launch) or
            // the SwitchSegmentSession-armed path (Case A) takes precedence. Resetting
            // here — rather than at every recorder/activeTree teardown site — guarantees
            // a prior craft's resume UT cannot leak into a later unrelated tree's dialog.
            if (!double.IsNaN(liveResumeSessionStartUT))
            {
                ParsekLog.Verbose("Flight",
                    $"liveResumeSessionStartUT cleared reason={reason} " +
                    $"prevUt={liveResumeSessionStartUT.ToString("R", CultureInfo.InvariantCulture)}");
            }
            liveResumeSessionStartUT = double.NaN;

            // Reset crash coalescer on every recorder start so a fresh recorder cannot
            // inherit a stale coalescing window from the previous segment or scene.
            if (crashCoalescer != null)
            {
                crashCoalescer.Reset();
                ParsekLog.Verbose("Coalescer", "Coalescer reset on recording start");
            }

            // A newly-started recorder owns split detection from a clean slate.
            pendingDeferredSplitCheckTrigger = DeferredSplitCheckTrigger.None;
            pendingSplitTriggerUT = double.NaN;
            preBreakVesselPids = null;

            bool subscribedDecoupleListener = PrepareRecorderStartCollections(
                ref decoupleCreatedVessels,
                ref decoupleControllerStatus,
                ref decoupleCreatedTrajectoryPoints);
            if (subscribedDecoupleListener)
                GameEvents.onPartDeCoupleNewVesselComplete.Add(OnDecoupleNewVesselDuringSplitCheck);

            ParsekLog.Verbose("Flight",
                $"PrepareSessionStateForRecorderStart: reason={reason}, " +
                $"decoupleListener={(subscribedDecoupleListener ? "subscribed" : "reused")}");
        }

        internal static bool PrepareRecorderStartCollections<TCapturedVessel, TTrajectorySeed>(
            ref List<TCapturedVessel> createdVessels,
            ref Dictionary<uint, bool> controllerStatus,
            ref Dictionary<uint, TTrajectorySeed> trajectorySeeds)
        {
            bool shouldSubscribeDecoupleListener = false;

            if (createdVessels == null)
            {
                createdVessels = new List<TCapturedVessel>();
                shouldSubscribeDecoupleListener = true;
            }
            else
            {
                createdVessels.Clear();
            }

            if (controllerStatus == null)
                controllerStatus = new Dictionary<uint, bool>();
            else
                controllerStatus.Clear();

            if (trajectorySeeds == null)
                trajectorySeeds = new Dictionary<uint, TTrajectorySeed>();
            else
                trajectorySeeds.Clear();

            return shouldSubscribeDecoupleListener;
        }

        public void StopRecording()
        {
            ParsekLog.Info("Flight", "StopRecording called");
            // Unsubscribe decouple listener
            if (decoupleCreatedVessels != null)
            {
                GameEvents.onPartDeCoupleNewVesselComplete.Remove(OnDecoupleNewVesselDuringSplitCheck);
                decoupleCreatedVessels = null;
                decoupleControllerStatus = null;
                decoupleCreatedTrajectoryPoints = null;
            }
            decoupleOriginByNewPid.Clear();
            pendingDeferredSplitCheckTrigger = DeferredSplitCheckTrigger.None;
            pendingSplitTriggerUT = double.NaN;
            recorder?.StopRecording();

            // Tag final segment phase if untagged
            if (recorder?.CaptureAtStop != null
                && string.IsNullOrEmpty(recorder.CaptureAtStop.SegmentPhase)
                && SegmentPhaseClassifier.TryClassify(
                    FlightGlobals.ActiveVessel,
                    out string phase,
                    out string bodyName))
            {
                recorder.CaptureAtStop.SegmentBodyName = bodyName;
                recorder.CaptureAtStop.SegmentPhase = phase;
                ParsekLog.Verbose("Flight", $"Final segment tagged (StopRecording): " +
                    $"{recorder.CaptureAtStop.SegmentBodyName} {recorder.CaptureAtStop.SegmentPhase}");
            }
        }
    }
}
