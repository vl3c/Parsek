using System;
using System.Collections.Generic;
using System.Globalization;
using ClickThroughFix;
using KSP.UI.Screens;
using ToolbarControl_NS;
using UnityEngine;

namespace Parsek
{
    /// <summary>
    /// KSC scene host for the Parsek UI and passive ghost playback.
    /// Shows committed recording ghosts (rockets launching, planes flying)
    /// as visual-only objects in the KSC view with full part-event fidelity
    /// (engine plumes, staging, parachutes, lights, etc.).
    /// </summary>
    [KSPAddon(KSPAddon.Startup.SpaceCentre, false)]
    public class ParsekKSC : MonoBehaviour
    {
        private ToolbarControl toolbarControl;
        private ParsekUI ui;
        private bool showUI;
        private Rect windowRect = new Rect(20, 100, 200, 10);

        // KSC ghost playback state — keyed by recording index in CommittedRecordings
        private Dictionary<int, GhostPlaybackState> kscGhosts =
            new Dictionary<int, GhostPlaybackState>();

        // Overlap ghosts for negative loop intervals (multiple simultaneous ghosts per recording)
        private Dictionary<int, List<GhostPlaybackState>> kscOverlapGhosts =
            new Dictionary<int, List<GhostPlaybackState>>();

        // Cached body lookup to avoid per-frame lambda allocations
        private Dictionary<string, CelestialBody> bodyCache;

        // One-time log tracking (avoids repeating the same log every frame)
        private HashSet<int> loggedGhostSpawn = new HashSet<int>();
        private HashSet<int> loggedReshow = new HashSet<int>();

        // #443: Non-spamming cadence-adjustment log — one INFO per
        // (recording index, userPeriod, effectiveCadence, duration) tuple.
        private Dictionary<int, (double userPeriod, double effectiveCadence, double duration)>
            lastLoggedKscCadence = new Dictionary<int, (double, double, double)>();
        private readonly Dictionary<int, GhostPlaybackLogic.AutoLoopLaunchSchedule>
            autoLoopLaunchSchedules = new Dictionary<int, GhostPlaybackLogic.AutoLoopLaunchSchedule>();
        private readonly List<AutoLoopQueueCandidate> autoLoopQueueScratch = new List<AutoLoopQueueCandidate>();

        // KSC spawn dedup: tracks recording IDs that have had spawn attempted (bug #99)
        private HashSet<string> kscSpawnAttempted = new HashSet<string>();
        private HashSet<string> loggedPlaybackDisabledPastEndSpawnAttempts = new HashSet<string>();
        private bool pauseMenuOpen;
        internal static Action<GhostPlaybackState> PauseGhostAudioAction = GhostPlaybackLogic.PauseAllAudio;
        internal static Action<GhostPlaybackState> UnpauseGhostAudioAction = GhostPlaybackLogic.UnpauseAllAudio;

        // Tunables live in ParsekConfig.cs. KSC reuses the flight cap
        // (GhostPlayback.MaxOverlapGhostsPerRecording) so the two scenes stay
        // in lockstep.

        // Distance culling: skip part events and deactivate ghosts beyond this range from camera.
        // 25km matches Kerbal Konstructs' default activation range for statics.
        private const float GhostCullDistanceSq = DistanceThresholds.KscGhosts.CullDistanceSq;

        private readonly struct AutoLoopQueueCandidate
        {
            internal AutoLoopQueueCandidate(
                int recordingIndex,
                double playbackStartUT,
                double playbackEndUT,
                string recordingId)
            {
                RecordingIndex = recordingIndex;
                PlaybackStartUT = playbackStartUT;
                PlaybackEndUT = playbackEndUT;
                RecordingId = recordingId ?? string.Empty;
            }

            internal int RecordingIndex { get; }
            internal double PlaybackStartUT { get; }
            internal double PlaybackEndUT { get; }
            internal string RecordingId { get; }
        }

        private static int CompareAutoLoopQueueCandidates(AutoLoopQueueCandidate a, AutoLoopQueueCandidate b)
        {
            int cmp = a.PlaybackStartUT.CompareTo(b.PlaybackStartUT);
            if (cmp != 0)
                return cmp;

            cmp = a.PlaybackEndUT.CompareTo(b.PlaybackEndUT);
            if (cmp != 0)
                return cmp;

            cmp = string.CompareOrdinal(a.RecordingId, b.RecordingId);
            if (cmp != 0)
                return cmp;

            return a.RecordingIndex.CompareTo(b.RecordingIndex);
        }

        void Start()
        {
            ParsekLog.Info("KSC", "ParsekKSC starting in Space Center scene");

            ui = new ParsekUI(UIMode.KSC);
            GameEvents.onGamePause.Add(OnGamePause);
            GameEvents.onGameUnpause.Add(OnGameUnpause);

            toolbarControl = gameObject.AddComponent<ToolbarControl>();
            toolbarControl.AddToAllToolbars(
                () => { showUI = true; ParsekLog.Verbose("KSC", "Toolbar button ON"); },
                () => { showUI = false; ParsekLog.Verbose("KSC", "Toolbar button OFF"); },
                ApplicationLauncher.AppScenes.SPACECENTER,
                ParsekFlight.MODID, "parsekKSCButton",
                "Parsek/Textures/parsek_64",
                "Parsek/Textures/parsek_32",
                ParsekFlight.MODNAME
            );

            ui.CloseMainWindow = () =>
            {
                showUI = false;
                if (toolbarControl != null) toolbarControl.SetFalse();
            };

            // Build body lookup cache
            bodyCache = new Dictionary<string, CelestialBody>();
            if (FlightGlobals.Bodies != null)
            {
                for (int i = 0; i < FlightGlobals.Bodies.Count; i++)
                {
                    var b = FlightGlobals.Bodies[i];
                    if (b != null && !string.IsNullOrEmpty(b.name))
                        bodyCache[b.name] = b;
                }
                ParsekLog.Verbose("KSC", $"Body cache built: {bodyCache.Count} bodies");
            }

            int ghostCount = 0;
            // [ERS-exempt] reason: ParsekKSC keys ghost dictionaries by committed
            // recording index (kscGhosts[i]); converting to ERS here would break
            // the index<->ghost correspondence.
            // TODO(phase 6+): migrate ParsekKSC to EffectiveRecordingId-keyed ghost dicts.
            var committed = RecordingStore.CommittedRecordings;
            for (int i = 0; i < committed.Count; i++)
            {
                var rec = committed[i];
                if (ShouldShowInKSC(rec))
                {
                    ghostCount++;
                    ParsekLog.Verbose("KSCGhost",
                        $"Recording #{i} \"{rec.VesselName}\" eligible: " +
                        $"UT=[{rec.StartUT:F1},{rec.EndUT:F1}] loop={rec.LoopPlayback} " +
                        $"terminal={rec.TerminalStateValue} points={rec.Points.Count}");
                }
            }

            ParsekLog.Info("KSC",
                $"ParsekKSC initialized, {committed.Count} committed recordings, " +
                $"{ghostCount} eligible for KSC ghost playback");
        }

        void OnGUI()
        {
            if (!showUI) return;

            windowRect.height = 0f;
            var opaqueWindowStyle = ui.GetOpaqueWindowStyle();
            if (opaqueWindowStyle == null)
                return;

            ParsekUI.ResetWindowGuiColors(out Color prevColor, out Color prevBackgroundColor, out Color prevContentColor);
            try
            {
                windowRect = ClickThruBlocker.GUILayoutWindow(
                    GetInstanceID(), windowRect, ui.DrawWindow,
                    "Parsek", opaqueWindowStyle, GUILayout.Width(250));
            }
            finally
            {
                ParsekUI.RestoreWindowGuiColors(prevColor, prevBackgroundColor, prevContentColor);
            }

            ui.DrawRecordingsWindowIfOpen(windowRect);
            ui.DrawTimelineWindowIfOpen(windowRect);
            ui.DrawKerbalsWindowIfOpen(windowRect);
            ui.DrawCareerStateWindowIfOpen(windowRect);
            ui.DrawSettingsWindowIfOpen(windowRect);
            ui.DrawTestRunnerWindowIfOpen(windowRect, this);
        }

        #region Ghost Playback

        void Update()
        {
            // During rewind, Planetarium UT is still the pre-rewind future value until
            // the deferred coroutine sets the correct UT. Skip all playback + spawn logic
            // to prevent future ghosts and premature vessel spawns.
            if (RecordingStore.RewindUTAdjustmentPending) return;

            // [ERS-exempt] reason: same as constructor — ParsekKSC keys ghost
            // state by committed recording index. See TODO(phase 6+) above.
            var committed = RecordingStore.CommittedRecordings;
            if (committed.Count == 0) return;

            double currentUT = Planetarium.GetUniversalTime();

            float warpRate = TimeWarp.CurrentRate;
            bool suppressGhosts = GhostPlaybackLogic.ShouldSuppressGhosts(warpRate);
            bool suppressVisualFx = GhostPlaybackLogic.ShouldSuppressVisualFx(warpRate);
            RebuildAutoLoopLaunchScheduleCache(committed);

            if (suppressGhosts)
                loggedReshow.Clear();

            for (int i = 0; i < committed.Count; i++)
            {
                var rec = committed[i];

                // Structural reject (non-Kerbin, too-short trajectory): no KSC render path
                // and no KSC spawn path. Clean up any leftover ghosts and skip silently.
                if (!IsKscStructurallyEligible(rec))
                {
                    if (kscGhosts.ContainsKey(i))
                    {
                        ParsekLog.Verbose("KSCGhost",
                            $"Ghost #{i} \"{rec.VesselName}\" not structurally eligible — destroying");
                        DestroyKscGhost(kscGhosts[i], i);
                        kscGhosts.Remove(i);
                        loggedGhostSpawn.Remove(i);
                    }
                    DestroyAllKscOverlapGhosts(i);
                    continue;
                }

                // Visibility-only reject (PlaybackEnabled=false): ghost must not render,
                // but the recording's career effect still applies — so once past-end,
                // drive the same persistent-vessel spawn path the visible branch would.
                // Bug #433.
                if (!rec.PlaybackEnabled)
                {
                    if (kscGhosts.ContainsKey(i))
                    {
                        ParsekLog.Verbose("KSCGhost",
                            $"Ghost #{i} \"{rec.VesselName}\" playback disabled — destroying visual");
                        DestroyKscGhost(kscGhosts[i], i);
                        kscGhosts.Remove(i);
                        loggedGhostSpawn.Remove(i);
                    }
                    DestroyAllKscOverlapGhosts(i);
                    if (currentUT > rec.EndUT)
                    {
                        LogPlaybackDisabledPastEndSpawnAttemptOnce(
                            rec,
                            i,
                            "playback-disabled-past-end",
                            loggedPlaybackDisabledPastEndSpawnAttempts);
                        TrySpawnAtRecordingEnd(i, rec);
                    }
                    continue;
                }

                // Branch: looping recordings — #381 dispatch on period < duration (overlap).
                if (rec.LoopPlayback)
                {
                    double duration = GhostPlaybackEngine.EffectiveLoopDuration(rec);
                    if (duration <= LoopTiming.MinLoopDurationSeconds) continue;

                    double intervalSeconds = GetLoopIntervalSeconds(rec, i, autoLoopLaunchSchedules);
                    if (GhostPlaybackLogic.IsOverlapLoop(intervalSeconds, duration))
                    {
                        // Period < duration: successive launches overlap, multi-ghost path.
                        if (TryGetLoopSchedule(
                                rec,
                                i,
                                autoLoopLaunchSchedules,
                                out double playbackStartUT,
                                out double scheduleStartUT,
                                out _,
                                out _))
                        {
                            UpdateOverlapKsc(
                                i,
                                rec,
                                currentUT,
                                intervalSeconds,
                                duration,
                                playbackStartUT,
                                scheduleStartUT,
                                warpRate,
                                suppressGhosts,
                                suppressVisualFx);
                        }
                        continue;
                    }

                    // Period >= duration: single-ghost path — clean up any leftover overlaps
                    DestroyAllKscOverlapGhosts(i);

                    double targetUT;
                    long cycleIndex;
                    bool inPauseWindow;
                    bool inRange = TryComputeLoopUT(rec, currentUT,
                        out targetUT, out cycleIndex, out inPauseWindow, i, autoLoopLaunchSchedules);

                    UpdateSingleGhostKsc(i, rec, currentUT, targetUT, cycleIndex,
                        inRange, inPauseWindow, warpRate, suppressGhosts, suppressVisualFx);
                }
                else
                {
                    // Non-looping: raw UT range check
                    DestroyAllKscOverlapGhosts(i);
                    bool inRange = currentUT >= rec.StartUT && currentUT <= rec.EndUT;
                    UpdateSingleGhostKsc(i, rec, currentUT, currentUT, 0, inRange, false,
                        warpRate, suppressGhosts, suppressVisualFx);
                }
            }
        }

        private void RebuildAutoLoopLaunchScheduleCache(IReadOnlyList<Recording> recordings)
        {
            autoLoopLaunchSchedules.Clear();
            autoLoopQueueScratch.Clear();
            if (recordings == null || recordings.Count == 0)
                return;

            double globalInterval = ParsekSettings.Current?.autoLoopIntervalSeconds
                                    ?? LoopTiming.DefaultLoopIntervalSeconds;
            for (int i = 0; i < recordings.Count; i++)
            {
                Recording recording = recordings[i];
                if (!GhostPlaybackLogic.ShouldUseGlobalAutoLaunchQueue(recording))
                    continue;

                autoLoopQueueScratch.Add(new AutoLoopQueueCandidate(
                    i,
                    GhostPlaybackEngine.EffectiveLoopStartUT(recording),
                    GhostPlaybackEngine.EffectiveLoopEndUT(recording),
                    recording.RecordingId));
            }

            if (autoLoopQueueScratch.Count == 0)
                return;

            autoLoopQueueScratch.Sort(CompareAutoLoopQueueCandidates);
            double launchGapSeconds = GhostPlaybackLogic.ResolveLoopInterval(
                recordings[autoLoopQueueScratch[0].RecordingIndex],
                globalInterval,
                LoopTiming.DefaultLoopIntervalSeconds,
                LoopTiming.MinCycleDuration);
            double anchorUT = autoLoopQueueScratch[0].PlaybackStartUT;
            double cadenceSeconds = launchGapSeconds * autoLoopQueueScratch.Count;
            string orderedIds = BuildAutoLoopQueueOrderedIds(autoLoopQueueScratch);
            string fingerprint = BuildAutoLoopQueueFingerprint(autoLoopQueueScratch, anchorUT, cadenceSeconds);
            ParsekLog.VerboseOnChange("KSC",
                "auto-loop-queue",
                fingerprint,
                $"Auto loop queue rebuilt: count={autoLoopQueueScratch.Count} " +
                $"anchorUT={anchorUT.ToString("R", CultureInfo.InvariantCulture)} " +
                $"cadence={cadenceSeconds.ToString("R", CultureInfo.InvariantCulture)}s " +
                $"orderedIds={orderedIds}");
            for (int slot = 0; slot < autoLoopQueueScratch.Count; slot++)
            {
                AutoLoopQueueCandidate candidate = autoLoopQueueScratch[slot];
                autoLoopLaunchSchedules[candidate.RecordingIndex] =
                    new GhostPlaybackLogic.AutoLoopLaunchSchedule(
                        anchorUT + (slot * launchGapSeconds),
                        cadenceSeconds,
                        slot,
                        autoLoopQueueScratch.Count);
            }
        }

        private static string BuildAutoLoopQueueFingerprint(
            IReadOnlyList<AutoLoopQueueCandidate> queue,
            double anchorUT,
            double cadenceSeconds)
        {
            int count = queue?.Count ?? 0;
            return string.Format(
                CultureInfo.InvariantCulture,
                "count={0}|anchor={1}|cadence={2}|ids={3}",
                count,
                anchorUT.ToString("R", CultureInfo.InvariantCulture),
                cadenceSeconds.ToString("R", CultureInfo.InvariantCulture),
                BuildAutoLoopQueueOrderedIds(queue));
        }

        private static string BuildAutoLoopQueueOrderedIds(IReadOnlyList<AutoLoopQueueCandidate> queue)
        {
            if (queue == null || queue.Count == 0)
                return "(empty)";

            var ids = new List<string>(queue.Count);
            for (int i = 0; i < queue.Count; i++)
            {
                AutoLoopQueueCandidate candidate = queue[i];
                string id = string.IsNullOrEmpty(candidate.RecordingId)
                    ? "(no-id)"
                    : candidate.RecordingId;
                ids.Add(candidate.RecordingIndex.ToString(CultureInfo.InvariantCulture) + ":" + id);
            }

            return string.Join(",", ids.ToArray());
        }

        internal static bool LogPlaybackDisabledPastEndSpawnAttemptOnce(
            Recording rec,
            int recIdx,
            string reason,
            ISet<string> loggedKeys)
        {
            string safeReason = string.IsNullOrEmpty(reason)
                ? "playback-disabled-past-end"
                : reason;
            string id = !string.IsNullOrEmpty(rec?.RecordingId)
                ? rec.RecordingId
                : "idx:" + recIdx.ToString(CultureInfo.InvariantCulture);
            string key = safeReason + "|" + id;
            if (loggedKeys != null && !loggedKeys.Add(key))
                return false;

            ParsekLog.Verbose("KSCSpawn",
                $"Playback-disabled past-end: attempting spawn for #{recIdx} " +
                $"\"{rec?.VesselName ?? "(null)"}\" id={rec?.RecordingId ?? "(null)"} " +
                $"reason={safeReason}");
            return true;
        }

        /// <summary>
        /// Applies a ghost-audio action to all active KSC ghost states.
        /// Shared by the pause-menu handlers and unit-testable without Unity audio.
        /// </summary>
        internal static (int primaryCount, int overlapCount) ApplyAudioActionToGhostSet(
            Dictionary<int, GhostPlaybackState> primaryGhosts,
            Dictionary<int, List<GhostPlaybackState>> overlapGhosts,
            Action<GhostPlaybackState> applyAudioAction)
        {
            if (applyAudioAction == null)
                throw new ArgumentNullException(nameof(applyAudioAction));

            int primaryCount = 0;
            if (primaryGhosts != null)
            {
                foreach (var kvp in primaryGhosts)
                {
                    applyAudioAction(kvp.Value);
                    primaryCount++;
                }
            }

            int overlapCount = 0;
            if (overlapGhosts != null)
            {
                foreach (var kvp in overlapGhosts)
                {
                    if (kvp.Value == null) continue;
                    for (int i = 0; i < kvp.Value.Count; i++)
                    {
                        applyAudioAction(kvp.Value[i]);
                        overlapCount++;
                    }
                }
            }

            return (primaryCount, overlapCount);
        }

        internal (int primaryCount, int overlapCount) ApplyAudioActionToActiveGhosts(
            Action<GhostPlaybackState> applyAudioAction,
            string actionLabel)
        {
            var counts = ApplyAudioActionToGhostSet(kscGhosts, kscOverlapGhosts, applyAudioAction);
            ParsekLog.Verbose("GhostAudio",
                $"KSC {actionLabel}: {counts.primaryCount} primary + {counts.overlapCount} overlap ghost(s)");
            return counts;
        }

        internal static bool ShouldApplyRuntimeGhostEvents(bool pauseMenuOpen, bool inCullRange)
        {
            return !pauseMenuOpen && inCullRange;
        }

        void PauseGhostAudioIfMenuOpen(GhostPlaybackState state)
        {
            if (!pauseMenuOpen) return;
            GhostPlaybackLogic.PauseAllAudio(state);
        }

        /// <summary>
        /// Pauses all KSC ghost audio when the stock pause menu opens.
        /// </summary>
        void OnGamePause()
        {
            pauseMenuOpen = true;
            ApplyAudioActionToActiveGhosts(PauseGhostAudioAction, "OnGamePause");
        }

        /// <summary>
        /// Resumes all KSC ghost audio when the stock pause menu closes.
        /// </summary>
        void OnGameUnpause()
        {
            pauseMenuOpen = false;
            ApplyAudioActionToActiveGhosts(UnpauseGhostAudioAction, "OnGameUnpause");
        }

        /// <summary>
        /// Emits exactly one INFO per (recordingIndex, userPeriod,
        /// effectiveCadence, duration) tuple for the KSC overlap path.
        /// Re-emits only when one of the inputs changes. Steady-state silent.
        /// </summary>
        private void LogKscCadenceIfChanged(
            int recIdx, Recording rec,
            double userPeriod, double effectiveCadence, double duration)
        {
            var tuple = (userPeriod, effectiveCadence, duration);
            if (lastLoggedKscCadence.TryGetValue(recIdx, out var prev) && prev.Equals(tuple))
                return;
            lastLoggedKscCadence[recIdx] = tuple;

            string vesselName = rec != null ? rec.VesselName ?? "?" : "?";
            var ic = System.Globalization.CultureInfo.InvariantCulture;
            long cycleCount = duration > 0 && effectiveCadence > 0
                ? (long)Math.Ceiling(duration / effectiveCadence)
                : 0;
            bool adjusted = Math.Abs(effectiveCadence - userPeriod) > 1e-6;
            string verdict = adjusted
                ? "auto-adjusted (cap reached)"
                : "no adjustment";
            ParsekLog.Info("KSC",
                $"Loop cadence #{recIdx} \"{vesselName}\": requested={userPeriod.ToString("F2", ic)}s " +
                $"duration={duration.ToString("F2", ic)}s cap={GhostPlayback.MaxOverlapGhostsPerRecording} " +
                $"effective={effectiveCadence.ToString("F2", ic)}s (cycles={cycleCount}) {verdict}");
        }

        /// <summary>
        /// Single-ghost playback path (positive/zero loop interval, or non-looping).
        /// </summary>
        void UpdateSingleGhostKsc(int recIdx, Recording rec,
            double currentUT, double targetUT, long cycleIndex,
            bool inRange, bool inPauseWindow, float warpRate,
            bool suppressGhosts, bool suppressVisualFx)
        {
            GhostPlaybackState state;
            kscGhosts.TryGetValue(recIdx, out state);
            bool ghostActive = state != null && state.ghost != null;

            if (suppressGhosts && GhostPlaybackLogic.ShouldSuppressGhostMeshAtWarp(
                    warpRate, rec, targetUT))
            {
                if (ghostActive && state.ghost.activeSelf)
                {
                    state.ghost.SetActive(false);
                    ParsekLog.Verbose("KSCGhost",
                        $"Ghost #{recIdx} hidden: warp {warpRate.ToString("F1", CultureInfo.InvariantCulture)}x > {WarpThresholds.GhostHide}x");
                }
                return;
            }

            if (inRange && !inPauseWindow)
            {
                // Loop cycle change: destroy + respawn to guarantee clean visual state
                if (ghostActive && rec.LoopPlayback && state.loopCycleIndex != cycleIndex)
                {
                    long oldCycle = state.loopCycleIndex;
                    PositionGhostAtLoopEndpoint(recIdx, rec, state);
                    if (GhostPlaybackLogic.ShouldTriggerExplosionAtPlaybackUT(
                            rec, GhostPlaybackEngine.ResolveLoopPlaybackEndpointUT(rec)))
                    {
                        TriggerExplosionIfDestroyed(state, rec, recIdx);
                    }
                    DestroyKscGhost(state, recIdx);
                    kscGhosts.Remove(recIdx);
                    ghostActive = false;
                    state = null;
                    ParsekLog.Verbose("KSCGhost",
                        $"Ghost #{recIdx} \"{rec.VesselName}\" cycle change {oldCycle}→{cycleIndex}");
                }

                if (!ghostActive)
                {
                    state = SpawnKscGhost(rec, recIdx);
                    if (state == null) return;
                    state.loopCycleIndex = cycleIndex;
                    kscGhosts[recIdx] = state;
                    PauseGhostAudioIfMenuOpen(state);
                    if (loggedGhostSpawn.Add(recIdx))
                        ParsekLog.Verbose("KSCGhost",
                            $"Ghost #{recIdx} \"{rec.VesselName}\" entered range: " +
                            $"targetUT={targetUT:F1} recUT=[{rec.StartUT:F1},{rec.EndUT:F1}] " +
                            $"cycle={cycleIndex} loop={rec.LoopPlayback} " +
                            $"terminal={rec.TerminalStateValue}");
                }
                else if (!state.ghost.activeSelf)
                {
                    state.ghost.SetActive(true);
                    if (loggedReshow.Add(recIdx))
                        ParsekLog.Verbose("KSCGhost",
                            $"Ghost #{recIdx} \"{rec.VesselName}\" re-shown after warp-down");
                }

                InterpolateAndPositionKsc(
                    state.ghost, rec.Points,
                    ref state.playbackIndex, targetUT);

                // Distance culling: skip expensive part events for ghosts too far from camera
                if (ShouldApplyRuntimeGhostEvents(pauseMenuOpen, IsGhostInCullRange(state.ghost)))
                {
                    GhostPlaybackLogic.ApplyPartEvents(recIdx, rec, targetUT, state);
                    GhostPlaybackLogic.ApplyFlagEvents(state, rec, targetUT);
                }
                if (suppressVisualFx)
                    GhostPlaybackLogic.StopAllRcsEmissions(state);
                else
                    GhostPlaybackLogic.RestoreAllRcsEmissions(state);

                bool shouldTriggerExplosion = GhostPlaybackLogic.ShouldTriggerExplosionAtPlaybackUT(rec, targetUT);

                if (!state.explosionFired && shouldTriggerExplosion)
                    TriggerExplosionIfDestroyed(state, rec, recIdx);
            }
            else if (ghostActive)
            {
                if (inPauseWindow)
                {
                    PositionGhostAtLoopEndpoint(recIdx, rec, state);
                    if (!state.explosionFired
                        && GhostPlaybackLogic.ShouldTriggerExplosionAtPlaybackUT(
                            rec, GhostPlaybackEngine.ResolveLoopPlaybackEndpointUT(rec)))
                    {
                        TriggerExplosionIfDestroyed(state, rec, recIdx);
                    }
                    if (!state.pauseHidden)
                    {
                        state.pauseHidden = true;
                        GhostPlaybackLogic.HideAllGhostParts(state);
                        ParsekLog.Verbose("KSCGhost",
                            $"Ghost #{recIdx} \"{rec.VesselName}\" hidden during loop pause window");
                    }
                }
                else
                {
                    TriggerExplosionIfDestroyed(state, rec, recIdx);
                    // Spawn real vessel when ghost timeline completes (bug #99)
                    TrySpawnAtRecordingEnd(recIdx, rec);
                    ParsekLog.Verbose("KSCGhost",
                        $"Ghost #{recIdx} \"{rec.VesselName}\" exited range at UT {currentUT:F1}");
                    DestroyKscGhost(state, recIdx);
                    kscGhosts.Remove(recIdx);
                    loggedGhostSpawn.Remove(recIdx);
                }
            }
            else if (!inRange && !inPauseWindow && currentUT > rec.EndUT)
            {
                // Ghost was never created (e.g., time-warped through the entire window)
                // but recording is past its end — still need to spawn the vessel (bug #99)
                TrySpawnAtRecordingEnd(recIdx, rec);
            }
        }

        /// <summary>
        /// Multi-ghost overlap path for negative loop intervals.
        /// Multiple ghosts from different cycles visible simultaneously.
        /// Simplified version of ParsekFlight.UpdateOverlapLoopPlayback
        /// (no camera logic, no reentry FX).
        /// </summary>
        void UpdateOverlapKsc(int recIdx, Recording rec,
            double currentUT, double intervalSeconds, double duration,
            double playbackStartUT, double scheduleStartUT,
            float warpRate, bool suppressGhosts, bool suppressVisualFx)
        {
            GhostPlaybackState primaryState;
            kscGhosts.TryGetValue(recIdx, out primaryState);
            bool primaryActive = primaryState != null && primaryState.ghost != null;

            if (currentUT < scheduleStartUT)
            {
                if (primaryActive) { DestroyKscGhost(primaryState, recIdx); kscGhosts.Remove(recIdx); }
                DestroyAllKscOverlapGhosts(recIdx);
                return;
            }

            // #443: effective cadence doubles the user period until
            // ceil(duration/cadence) <= MaxOverlapGhostsPerRecording, so the
            // per-recording cap is never exceeded and no cycle is silently
            // culled mid-trajectory.
            double effectiveCadence = GhostPlaybackLogic.ComputeEffectiveLaunchCadence(
                intervalSeconds, duration, GhostPlayback.MaxOverlapGhostsPerRecording);
            LogKscCadenceIfChanged(recIdx, rec, intervalSeconds, effectiveCadence, duration);

            double cycleDuration = Math.Max(effectiveCadence, LoopTiming.MinCycleDuration);

            long firstCycle, lastCycle;
            GhostPlaybackLogic.GetActiveCycles(currentUT, scheduleStartUT, scheduleStartUT + duration,
                effectiveCadence, GhostPlayback.MaxOverlapGhostsPerRecording, out firstCycle, out lastCycle);

            // Ensure overlap list exists
            List<GhostPlaybackState> overlaps;
            if (!kscOverlapGhosts.TryGetValue(recIdx, out overlaps))
            {
                overlaps = new List<GhostPlaybackState>();
                kscOverlapGhosts[recIdx] = overlaps;
            }

            // --- Primary ghost = newest cycle (lastCycle) ---
            // primaryActive already guarantees primaryState != null && ghost != null
            bool primaryCycleChanged = !primaryActive
                || primaryState.loopCycleIndex != lastCycle;
            double primaryLoopUT = GhostPlaybackLogic.ComputeOverlapCyclePlaybackUT(
                currentUT,
                scheduleStartUT,
                playbackStartUT,
                duration,
                cycleDuration,
                lastCycle);

            if (suppressGhosts)
            {
                DestroyAllKscOverlapGhosts(recIdx);

                if (GhostPlaybackLogic.ShouldSuppressGhostMeshAtWarp(
                        warpRate, rec, primaryLoopUT))
                {
                    if (primaryActive && primaryState.ghost.activeSelf)
                    {
                        primaryState.ghost.SetActive(false);
                        ParsekLog.Verbose("KSCGhost",
                            $"Ghost #{recIdx} hidden: warp {warpRate.ToString("F1", CultureInfo.InvariantCulture)}x > {WarpThresholds.GhostHide}x");
                    }
                    return;
                }

                // Keep the newest stationary primary mesh only during high warp.
            }

            if (primaryCycleChanged)
            {
                if (primaryActive)
                {
                    kscGhosts.Remove(recIdx);
                    if (suppressGhosts)
                    {
                        // Do not accumulate overlap clones while high-warp culling is active.
                        DestroyKscGhost(primaryState, recIdx);
                    }
                    else
                    {
                        // Move old primary to the overlap list so it keeps playing.
                        overlaps.Add(primaryState);
                        ParsekLog.Verbose("KSCGhost",
                            $"Ghost #{recIdx} cycle={primaryState.loopCycleIndex} moved to overlap list");
                    }
                }

                // Spawn new primary for lastCycle
                primaryState = SpawnKscGhost(rec, recIdx);
                if (primaryState == null) return;
                primaryState.loopCycleIndex = lastCycle;
                kscGhosts[recIdx] = primaryState;
                PauseGhostAudioIfMenuOpen(primaryState);
                ParsekLog.Verbose("KSCGhost",
                    $"Ghost #{recIdx} \"{rec.VesselName}\" overlap spawn cycle={lastCycle}");
            }

            // Position and animate primary (SpawnKscGhost above guarantees non-null)
            {
                double loopUT = primaryLoopUT;

                InterpolateAndPositionKsc(primaryState.ghost, rec.Points,
                    ref primaryState.playbackIndex, loopUT);

                if (ShouldApplyRuntimeGhostEvents(pauseMenuOpen, IsGhostInCullRange(primaryState.ghost)))
                {
                    GhostPlaybackLogic.ApplyPartEvents(recIdx, rec, loopUT, primaryState);
                    GhostPlaybackLogic.ApplyFlagEvents(primaryState, rec, loopUT);
                }
                if (suppressVisualFx)
                    GhostPlaybackLogic.StopAllRcsEmissions(primaryState);
                else
                    GhostPlaybackLogic.RestoreAllRcsEmissions(primaryState);

                bool shouldTriggerExplosion = GhostPlaybackLogic.ShouldTriggerExplosionAtPlaybackUT(rec, loopUT);

                if (!primaryState.explosionFired && shouldTriggerExplosion)
                    TriggerExplosionIfDestroyed(primaryState, rec, recIdx);
            }

            // --- Overlap ghosts (older cycles, reverse iterate) ---
            for (int j = overlaps.Count - 1; j >= 0; j--)
            {
                var ovState = overlaps[j];
                if (ovState == null || ovState.ghost == null)
                {
                    overlaps.RemoveAt(j);
                    continue;
                }

                long cycle = ovState.loopCycleIndex;
                double phase = currentUT - (scheduleStartUT + cycle * cycleDuration);

                if (phase > duration)
                {
                    // Cycle expired — position at end, explode, destroy
                    PositionGhostAtLoopEndpoint(recIdx, rec, ovState);
                    if (GhostPlaybackLogic.ShouldTriggerExplosionAtPlaybackUT(
                            rec, GhostPlaybackEngine.ResolveLoopPlaybackEndpointUT(rec)))
                    {
                        TriggerExplosionIfDestroyed(ovState, rec, recIdx);
                    }
                    ParsekLog.Verbose("KSCGhost",
                        $"Ghost #{recIdx} overlap cycle={cycle} expired, destroying");
                    DestroyKscGhost(ovState, recIdx);
                    overlaps.RemoveAt(j);
                    continue;
                }

                if (phase < 0) phase = 0;
                double loopUT = playbackStartUT + phase;

                InterpolateAndPositionKsc(ovState.ghost, rec.Points,
                    ref ovState.playbackIndex, loopUT);

                if (ShouldApplyRuntimeGhostEvents(pauseMenuOpen, IsGhostInCullRange(ovState.ghost)))
                {
                    GhostPlaybackLogic.ApplyPartEvents(recIdx, rec, loopUT, ovState);
                    GhostPlaybackLogic.ApplyFlagEvents(ovState, rec, loopUT);
                }
                if (suppressVisualFx)
                    GhostPlaybackLogic.StopAllRcsEmissions(ovState);
                else
                    GhostPlaybackLogic.RestoreAllRcsEmissions(ovState);

                if (!ovState.explosionFired
                    && GhostPlaybackLogic.TryGetEarlyDestroyedDebrisExplosionUT(rec, out double earlyExplosionUT)
                    && loopUT >= earlyExplosionUT)
                {
                    TriggerExplosionIfDestroyed(ovState, rec, recIdx);
                }
            }
        }

        void PositionGhostAtLoopEndpoint(int recIdx, Recording rec, GhostPlaybackState state)
        {
            if (state?.ghost == null || rec?.Points == null || rec.Points.Count == 0)
                return;

            double endpointUT = GhostPlaybackEngine.ResolveLoopPlaybackEndpointUT(rec);
            InterpolateAndPositionKsc(state.ghost, rec.Points, ref state.playbackIndex, endpointUT);
        }

        /// <summary>
        /// Destroy all overlap ghosts for a recording.
        /// Called when transitioning from negative to positive interval, or on cleanup.
        /// </summary>
        void DestroyAllKscOverlapGhosts(int recIdx)
        {
            List<GhostPlaybackState> list;
            if (!kscOverlapGhosts.TryGetValue(recIdx, out list)) return;
            if (list.Count > 0)
                ParsekLog.Verbose("KSCGhost",
                    $"Destroying {list.Count} overlap ghost(s) for recording #{recIdx}");
            for (int i = 0; i < list.Count; i++)
                DestroyKscGhost(list[i], recIdx);
            list.Clear();
        }

        /// <summary>
        /// Structural KSC eligibility: the recording describes something that can be
        /// rendered as a KSC ghost at all (Kerbin body, non-trivial trajectory).
        /// Does not consider the user-facing PlaybackEnabled visibility toggle — that
        /// lives in <see cref="ShouldShowInKSC"/>. Bug #433: separating the two lets
        /// the Update loop still fire past-end spawns for visibility-hidden recordings
        /// without starting spawns for non-Kerbin or too-short ones.
        /// </summary>
        internal static bool IsKscStructurallyEligible(Recording rec)
        {
            if (rec.Points == null || rec.Points.Count < 2) return false;
            // Only Kerbin recordings (KSC is on Kerbin)
            if (rec.Points[0].bodyName != "Kerbin") return false;
            return true;
        }

        /// <summary>
        /// Filter recordings for KSC ghost display. Combines structural eligibility
        /// with the user's visibility toggle.
        /// </summary>
        internal static bool ShouldShowInKSC(Recording rec)
        {
            return IsKscStructurallyEligible(rec) && rec.PlaybackEnabled;
        }

        /// <summary>
        /// Spawn a ghost for KSC playback. Returns null if no snapshot available.
        /// Simplified version of ParsekFlight.SpawnTimelineGhost — no camera pivot,
        /// no reentry FX, no sphere fallback.
        /// </summary>
        GhostPlaybackState SpawnKscGhost(Recording rec, int index)
        {
            // Skip if no snapshot — no sphere fallback in KSC
            var snapshot = GhostVisualBuilder.GetGhostSnapshot(rec);
            if (snapshot == null)
            {
                ParsekLog.VerboseRateLimited("KSCGhost", $"spawn-no-snapshot-{index}",
                    $"Ghost #{index} \"{rec.VesselName}\": no snapshot, skipping");
                return null;
            }

            var buildResult = GhostVisualBuilder.BuildTimelineGhostFromSnapshot(
                rec, $"Parsek_KSC_{index}");

            if (buildResult == null)
            {
                ParsekLog.Verbose("KSCGhost",
                    $"Ghost #{index} \"{rec.VesselName}\": BuildTimelineGhostFromSnapshot returned null");
                return null;
            }

            GameObject ghost = buildResult.root;

            var state = new GhostPlaybackState
            {
                ghost = ghost,
                // cameraPivot intentionally null — RecalculateCameraPivot no-ops
                // reentryFxInfo intentionally null — RebuildReentryMeshes no-ops
                playbackIndex = 0,
                partEventIndex = 0,
                partTree = GhostVisualBuilder.BuildPartSubtreeMap(snapshot),
                logicalPartIds = GhostVisualBuilder.BuildSnapshotPartIdSet(snapshot)
            };

            GhostPlaybackLogic.PopulateGhostInfoDictionaries(state, buildResult);

            GhostPlaybackLogic.InitializeInventoryPlacementVisibility(rec, state);
            GhostPlaybackLogic.RefreshCompoundPartVisibility(state);

            // Initialize flag event index — flags are spawned as real vessels on-demand by ApplyFlagEvents
            GhostPlaybackLogic.InitializeFlagVisibility(rec, state);

            ParsekLog.Verbose("KSCGhost",
                $"Ghost #{index} \"{rec.VesselName}\" spawned" +
                $" (engines={buildResult.engineInfos?.Count ?? 0}" +
                $" rcs={buildResult.rcsInfos?.Count ?? 0}" +
                $" lights={buildResult.lightInfos?.Count ?? 0}" +
                $" deployables={buildResult.deployableInfos?.Count ?? 0}" +
                $" fairings={buildResult.fairingInfos?.Count ?? 0}" +
                $" parachutes={buildResult.parachuteInfos?.Count ?? 0})");

            return state;
        }

        /// <summary>
        /// Position a ghost by interpolating between trajectory points.
        /// Simplified version — no FloatingOrigin GhostPosEntry registration
        /// (positions are recomputed from lat/lon/alt each frame, so origin
        /// shifts are handled automatically).
        /// Stops positioning when the trajectory leaves Kerbin.
        /// </summary>
        internal void InterpolateAndPositionKsc(
            GameObject ghost, List<TrajectoryPoint> points,
            ref int cachedIndex, double targetUT)
        {
            TrajectoryPoint before, after;
            float t;
            bool hasSegment = TrajectoryMath.InterpolatePoints(
                points, ref cachedIndex, targetUT, out before, out after, out t);

            if (!hasSegment)
            {
                // Either empty list or before recording start
                if (points == null || points.Count == 0)
                {
                    ghost.SetActive(false);
                    return;
                }
                PositionGhostAtPoint(ghost, before);
                return;
            }

            // Degenerate segment (t == 0 from zero-duration segment)
            if (t == 0f && before.ut == after.ut)
            {
                PositionGhostAtPoint(ghost, before);
                return;
            }

            // Stop positioning when trajectory leaves Kerbin
            if (before.bodyName != "Kerbin" || after.bodyName != "Kerbin")
            {
                ghost.SetActive(false);
                return;
            }

            CelestialBody bodyBefore = LookupBody(before.bodyName);
            CelestialBody bodyAfter = LookupBody(after.bodyName);
            if (bodyBefore == null || bodyAfter == null)
            {
                ParsekLog.VerboseRateLimited("KSCGhost", "interp-no-body",
                    $"Body not found: {(bodyBefore == null ? before.bodyName : after.bodyName)}");
                ghost.SetActive(false);
                return;
            }
            if (!ghost.activeSelf) ghost.SetActive(true);

            Vector3d posBefore = bodyBefore.GetWorldSurfacePosition(
                before.latitude, before.longitude, before.altitude);
            Vector3d posAfter = bodyAfter.GetWorldSurfacePosition(
                after.latitude, after.longitude, after.altitude);

            Vector3d interpolatedPos = Vector3d.Lerp(posBefore, posAfter, t);
            Quaternion interpolatedRot = Quaternion.Slerp(before.rotation, after.rotation, t);
            interpolatedRot = TrajectoryMath.SanitizeQuaternion(interpolatedRot);

            if (double.IsNaN(interpolatedPos.x) || double.IsNaN(interpolatedPos.y) ||
                double.IsNaN(interpolatedPos.z))
            {
                interpolatedPos = posBefore;
            }

            ghost.transform.position = interpolatedPos;
            ghost.transform.rotation = bodyBefore.bodyTransform.rotation * interpolatedRot;
        }

        /// <summary>
        /// Position ghost at a single trajectory point (no interpolation).
        /// </summary>
        void PositionGhostAtPoint(GameObject ghost, TrajectoryPoint point)
        {
            if (point.bodyName != "Kerbin")
            {
                ghost.SetActive(false);
                return;
            }

            CelestialBody body = LookupBody(point.bodyName);
            if (body == null) return;

            Vector3d worldPos = body.GetWorldSurfacePosition(
                point.latitude, point.longitude, point.altitude);

            Quaternion sanitized = TrajectoryMath.SanitizeQuaternion(point.rotation);
            ghost.transform.position = worldPos;
            ghost.transform.rotation = body.bodyTransform.rotation * sanitized;

            if (!ghost.activeSelf) ghost.SetActive(true);
        }

        /// <summary>
        /// Check if a ghost is within rendering distance of the KSC camera.
        /// Ghosts beyond 25km are deactivated to skip expensive part event processing.
        /// </summary>
        static bool IsGhostInCullRange(GameObject ghost)
        {
            if (ghost == null) return false;
            Camera cam = PlanetariumCamera.Camera;
            // Null camera during scene transitions — process all ghosts rather than skip
            if (cam == null) return true;
            float sqrDist = (ghost.transform.position - cam.transform.position).sqrMagnitude;
            if (sqrDist > GhostCullDistanceSq)
            {
                if (ghost.activeSelf) ghost.SetActive(false);
                return false;
            }
            if (!ghost.activeSelf) ghost.SetActive(true);
            return true;
        }

        /// <summary>
        /// Cached body lookup — avoids per-frame lambda allocation from FlightGlobals.Bodies.Find.
        /// </summary>
        CelestialBody LookupBody(string bodyName)
        {
            if (bodyCache != null)
            {
                CelestialBody cached;
                if (bodyCache.TryGetValue(bodyName, out cached))
                    return cached;
            }
            // Fallback if cache miss (shouldn't happen for Kerbin)
            return FlightGlobals.Bodies?.Find(b => b.name == bodyName);
        }

        /// <summary>
        /// Compute loop playback UT for a recording (positive/zero interval path only).
        /// Negative intervals use UpdateOverlapKsc with GetActiveCycles instead.
        /// Reimplemented from ParsekFlight.TryComputeLoopPlaybackUT (instance version)
        /// because the static 6-param overload doesn't return pause-window state.
        /// </summary>
        private static bool TryGetLoopSchedule(
            Recording rec,
            int recIdx,
            IReadOnlyDictionary<int, GhostPlaybackLogic.AutoLoopLaunchSchedule> autoLoopScheduleCache,
            out double playbackStartUT,
            out double scheduleStartUT,
            out double duration,
            out double intervalSeconds)
        {
            playbackStartUT = 0.0;
            scheduleStartUT = 0.0;
            duration = 0.0;
            intervalSeconds = 0.0;
            if (rec == null || !GhostPlaybackEngine.ShouldLoopPlayback(rec))
                return false;

            playbackStartUT = GhostPlaybackEngine.EffectiveLoopStartUT(rec);
            duration = GhostPlaybackEngine.EffectiveLoopDuration(rec);
            if (duration <= LoopTiming.MinLoopDurationSeconds)
                return false;

            double globalInterval = ParsekSettings.Current?.autoLoopIntervalSeconds
                                    ?? LoopTiming.DefaultLoopIntervalSeconds;
            double baseIntervalSeconds = GhostPlaybackLogic.ResolveLoopInterval(
                rec, globalInterval, LoopTiming.DefaultLoopIntervalSeconds, LoopTiming.MinCycleDuration);
            scheduleStartUT = playbackStartUT;
            intervalSeconds = baseIntervalSeconds;

            if (recIdx >= 0
                && autoLoopScheduleCache != null
                && autoLoopScheduleCache.TryGetValue(recIdx, out var cachedSchedule))
            {
                scheduleStartUT = cachedSchedule.LaunchStartUT;
                intervalSeconds = cachedSchedule.LaunchCadenceSeconds;
                return true;
            }

            if (recIdx >= 0 && GhostPlaybackLogic.ShouldUseGlobalAutoLaunchQueue(rec))
            {
                var committed = RecordingStore.CommittedRecordings;
                var trajectories = new List<IPlaybackTrajectory>(committed.Count);
                for (int i = 0; i < committed.Count; i++)
                    trajectories.Add(committed[i]);

                if (GhostPlaybackLogic.TryResolveAutoLoopLaunchSchedule(
                        trajectories,
                        recIdx,
                        baseIntervalSeconds,
                        out var autoSchedule))
                {
                    scheduleStartUT = autoSchedule.LaunchStartUT;
                    intervalSeconds = autoSchedule.LaunchCadenceSeconds;
                }
            }

            return true;
        }

        private static bool TryGetLoopSchedule(
            Recording rec,
            int recIdx,
            out double playbackStartUT,
            out double scheduleStartUT,
            out double duration,
            out double intervalSeconds)
        {
            return TryGetLoopSchedule(
                rec,
                recIdx,
                null,
                out playbackStartUT,
                out scheduleStartUT,
                out duration,
                out intervalSeconds);
        }

        internal static bool TryComputeLoopUT(
            Recording rec,
            double currentUT,
            out double loopUT,
            out long cycleIndex,
            out bool inPauseWindow,
            int recIdx = -1)
        {
            return TryComputeLoopUT(
                rec,
                currentUT,
                out loopUT,
                out cycleIndex,
                out inPauseWindow,
                recIdx,
                null);
        }

        internal static bool TryComputeLoopUT(
            Recording rec,
            double currentUT,
            out double loopUT,
            out long cycleIndex,
            out bool inPauseWindow,
            int recIdx,
            IReadOnlyDictionary<int, GhostPlaybackLogic.AutoLoopLaunchSchedule> autoLoopScheduleCache)
        {
            cycleIndex = 0;
            inPauseWindow = false;
            loopUT = 0.0;
            if (!TryGetLoopSchedule(
                    rec,
                    recIdx,
                    autoLoopScheduleCache,
                    out double playbackStartUT,
                    out double scheduleStartUT,
                    out double duration,
                    out double intervalSeconds))
                return false;

            if (!GhostPlaybackLogic.TryComputeLoopPlaybackPhase(
                    currentUT, scheduleStartUT, duration, intervalSeconds,
                    out double playbackPhase, out cycleIndex, out inPauseWindow))
            {
                return false;
            }

            loopUT = playbackStartUT + playbackPhase;
            return true;
        }

        /// <summary>
        /// Get the loop interval for a recording. Returns the launch-to-launch period
        /// in seconds (#381) — always &gt;= LoopTiming.MinCycleDuration.
        /// Overlap emerges when period &lt; recording duration (see IsOverlapLoop).
        /// </summary>
        internal static double GetLoopIntervalSeconds(Recording rec, int recIdx = -1)
        {
            return GetLoopIntervalSeconds(rec, recIdx, null);
        }

        internal static double GetLoopIntervalSeconds(
            Recording rec,
            int recIdx,
            IReadOnlyDictionary<int, GhostPlaybackLogic.AutoLoopLaunchSchedule> autoLoopScheduleCache)
        {
            if (TryGetLoopSchedule(
                    rec,
                    recIdx,
                    autoLoopScheduleCache,
                    out _,
                    out _,
                    out _,
                    out double intervalSeconds))
                return intervalSeconds;

            double globalInterval = ParsekSettings.Current?.autoLoopIntervalSeconds
                                    ?? LoopTiming.DefaultLoopIntervalSeconds;
            return GhostPlaybackLogic.ResolveLoopInterval(
                rec, globalInterval, LoopTiming.DefaultLoopIntervalSeconds, LoopTiming.MinCycleDuration);
        }

        /// <summary>
        /// Trigger explosion FX if the recording ended with vessel destruction.
        /// Guards against repeat firing via state.explosionFired.
        /// </summary>
        void TriggerExplosionIfDestroyed(GhostPlaybackState state,
            Recording rec, int recIdx)
        {
            if (state == null || state.ghost == null) return;
            if (state.explosionFired) return;
            if (rec.TerminalStateValue != TerminalState.Destroyed) return;

            if (GhostPlaybackLogic.ShouldSuppressVisualFx(TimeWarp.CurrentRate))
            {
                state.explosionFired = true;
                GhostPlaybackLogic.HideAllGhostParts(state);
                ParsekLog.VerboseRateLimited("KSCGhost", $"explosion-suppress-{recIdx}",
                    $"Explosion suppressed for ghost #{recIdx} \"{rec.VesselName}\": warp > {WarpThresholds.FxSuppress}x");
                return;
            }

            state.explosionFired = true;

            Vector3 worldPos = state.ghost.transform.position;
            // ComputeGhostLength excludes ParticleSystemRenderer bounds, so this
            // returns the actual vessel mesh size (not inflated by engine plume particles).
            float vesselLength = GhostVisualBuilder.ComputeGhostLength(state.ghost);

            ParsekLog.Info("KSCGhost",
                $"Explosion for ghost #{recIdx} \"{rec.VesselName}\" " +
                $"vesselLength={vesselLength:F1}m");

            // KSC scene has no FXMonger instance (it's a flight-scene MonoBehaviour); keep the custom FX here.
            var explosion = GhostVisualBuilder.SpawnExplosionFx(worldPos, vesselLength);
            if (explosion != null)
                Destroy(explosion, 6f);

            GhostPlaybackLogic.HideAllGhostParts(state);
        }

        /// <summary>
        /// Attempt to spawn a real vessel when a recording's ghost reaches end-of-timeline
        /// at KSC. Uses the same eligibility checks as Flight scene but simplified:
        /// no active chain concept, no collision detection (vessels are unloaded at KSC),
        /// no deferred spawn queue (no warp concern — KSC spawns are one-shot).
        /// The spawned vessel will appear in Tracking Station and persist in the save,
        /// but won't be loaded/physical at KSC (no physics range). Bug #99.
        /// </summary>
        void TrySpawnAtRecordingEnd(int recIdx, Recording rec)
        {
            // Looping recordings restart — never spawn at end
            if (rec.LoopPlayback)
            {
                ParsekLog.Verbose("KSCSpawn",
                    $"Spawn skipped for #{recIdx} \"{rec.VesselName}\": looping recording");
                return;
            }

            // Dedup: only attempt once per recording per scene session.
            // No logging — this fires every frame for every past-end recording (O(N*fps)).
            if (rec.RecordingId != null && !kscSpawnAttempted.Add(rec.RecordingId))
                return;

            // Use the KSC-specific eligibility check
            var (needsSpawn, reason) = GhostPlaybackLogic.ShouldSpawnAtKscEnd(rec);
            if (!needsSpawn)
            {
                ParsekLog.Info("KSCSpawn",
                    $"Spawn not needed for #{recIdx} \"{rec.VesselName}\": {reason}");
                return;
            }

            try
            {
                if (VesselSpawner.TryAdoptExistingSourceVesselForSpawn(
                    rec,
                    "KSCSpawn",
                    $"Spawn not needed for #{recIdx} \"{rec.VesselName}\""))
                    return;

                // At KSC, FlightGlobals.Vessels may be empty/null but
                // HighLogic.CurrentGame.flightState.protoVessels is always available.
                // RespawnVessel uses protoVessels directly - works in any scene.
                ParsekLog.Info("KSCSpawn",
                    $"Attempting spawn for #{recIdx} \"{rec.VesselName}\" (id={rec.RecordingId})");

                // Keep a private working snapshot for the entire KSC spawn flow so route
                // selection, fallback repairs, and aborts never mutate the stored recording.
                ConfigNode spawnSnapshot = rec.VesselSnapshot.CreateCopy();

                // Bug #167: apply crew swap directly on the KSC spawn snapshot because
                // there is no loaded vessel for SwapReservedCrewInFlight to target here.
                var replacements = CrewReservationManager.CrewReplacements;
                if (replacements.Count > 0)
                {
                    int swapped = CrewReservationManager.SwapReservedCrewInSnapshot(
                        spawnSnapshot, replacements);
                    if (swapped > 0)
                        ParsekLog.Info("KSCSpawn",
                            $"Crew swap applied to snapshot for #{recIdx} \"{rec.VesselName}\": " +
                            $"{swapped} crew replaced before spawn");
                    else
                        ParsekLog.Verbose("KSCSpawn",
                            $"Crew swap: {replacements.Count} reservation(s) exist but " +
                            $"no matches in snapshot for #{recIdx} \"{rec.VesselName}\"");
                }

                // Correct unsafe snapshot situation before spawning (#169).
                // Same guard as SpawnOrRecoverIfTooClose — prevents on-rails pressure destruction.
                VesselSpawner.CorrectUnsafeSnapshotSituation(spawnSnapshot, rec.TerminalStateValue);
                HashSet<string> excludeCrew = VesselSpawner.BuildExcludeCrewSet(rec);
                bool isEva = !string.IsNullOrEmpty(rec.EvaCrewName);
                bool isBreakupContinuous = rec.ChildBranchPointId != null && rec.TerminalStateValue.HasValue;
                bool routeThroughSpawnAtPosition = VesselSpawner.ShouldRouteThroughSpawnAtPosition(rec);
                bool useRecordedTerminalOrbit = VesselSpawner.ShouldUseRecordedTerminalOrbitSpawnState(rec, isEva);
                double spawnUT = Planetarium.GetUniversalTime();
                TrajectoryPoint? lastPt = rec.Points != null && rec.Points.Count > 0
                    ? (TrajectoryPoint?)rec.Points[rec.Points.Count - 1]
                    : null;
                CelestialBody body = VesselSpawner.ResolveSpawnRotationBody(rec, lastPt);
                double spawnLat = 0.0;
                double spawnLon = 0.0;
                double spawnAlt = 0.0;
                Vector3d spawnVelocity = Vector3d.zero;
                Orbit orbitalSpawnOrbit = null;
                bool haveResolvedSpawnState = false;

                if (lastPt.HasValue)
                {
                    VesselSpawner.ResolveSpawnPosition(
                        rec,
                        recIdx,
                        lastPt.Value,
                        out spawnLat,
                        out spawnLon,
                        out spawnAlt);
                    haveResolvedSpawnState = true;

                    if (useRecordedTerminalOrbit
                        && body != null
                        && VesselSpawner.TryResolveRecordedTerminalOrbitSpawnState(
                            rec,
                            body,
                            spawnUT,
                            out double orbitLat,
                            out double orbitLon,
                            out double orbitAlt,
                            out Vector3d orbitalSpawnVelocity,
                            out Orbit resolvedOrbit))
                    {
                        spawnLat = orbitLat;
                        spawnLon = orbitLon;
                        spawnAlt = orbitAlt;
                        spawnVelocity = orbitalSpawnVelocity;
                        orbitalSpawnOrbit = resolvedOrbit;
                    }
                    else
                    {
                        spawnVelocity = new Vector3d(
                            lastPt.Value.velocity.x,
                            lastPt.Value.velocity.y,
                            lastPt.Value.velocity.z);
                    }

                    if (isEva)
                    {
                        VesselSpawner.ApplyResolvedSpawnStateToSnapshot(
                            spawnSnapshot,
                            rec,
                            lastPt,
                            spawnLat,
                            spawnLon,
                            spawnAlt,
                            recIdx,
                            rec.VesselName);
                    }
                    else if (isBreakupContinuous)
                    {
                        VesselSpawner.ApplyResolvedSpawnStateToSnapshot(
                            spawnSnapshot,
                            rec,
                            lastPt,
                            spawnLat,
                            spawnLon,
                            spawnAlt,
                            recIdx,
                            rec.VesselName,
                            allowPreferredRotation: !useRecordedTerminalOrbit,
                            stripEvaLadder: false);
                    }
                    else if (VesselSpawner.IsSurfaceTerminal(rec.TerminalStateValue))
                    {
                        VesselSpawner.ApplyResolvedSpawnStateToSnapshot(
                            spawnSnapshot,
                            rec,
                            lastPt,
                            spawnLat,
                            spawnLon,
                            spawnAlt,
                            recIdx,
                            rec.VesselName,
                            stripEvaLadder: false);
                    }
                }

                if (!isEva
                    && VesselSpawner.ShouldBlockSpawnForDeadCrewInSnapshot(
                        spawnSnapshot,
                        out List<string> snapshotCrew))
                {
                    rec.VesselSpawned = true;
                    rec.SpawnAbandoned = true;
                    var classified = VesselSpawner.ClassifySnapshotCrew(snapshotCrew);
                    ParsekLog.Warn("KSCSpawn",
                        $"Spawn ABANDONED for #{recIdx} \"{rec.VesselName}\": no spawnable crew — " +
                        VesselSpawner.FormatSpawnableClassificationSummary(classified));
                    return;
                }

                uint spawnedPid = 0;
                if (routeThroughSpawnAtPosition && haveResolvedSpawnState)
                {
                    if (body != null)
                    {
                        Quaternion? surfaceRelativeRotationArg = null;
                        if (!useRecordedTerminalOrbit
                            && (isEva || isBreakupContinuous)
                            && VesselSpawner.TryGetPreferredSpawnRotationFrame(
                                rec,
                                lastPt,
                                out _,
                                out Quaternion preferredSurfaceRelativeRotation,
                                out _))
                        {
                            surfaceRelativeRotationArg = preferredSurfaceRelativeRotation;
                        }

                        spawnedPid = VesselSpawner.SpawnAtPosition(
                            spawnSnapshot,
                            body,
                            spawnLat,
                            spawnLon,
                            spawnAlt,
                            spawnVelocity,
                            spawnUT,
                            excludeCrew,
                            terminalState: rec.TerminalStateValue,
                            surfaceRelativeRotation: surfaceRelativeRotationArg,
                            orbitOverride: orbitalSpawnOrbit);
                        if (spawnedPid == 0)
                        {
                            ParsekLog.Warn("KSCSpawn",
                                $"SpawnAtPosition returned 0 for #{recIdx} \"{rec.VesselName}\" — " +
                                "falling back to validated snapshot respawn");
                        }
                    }
                    else
                    {
                        ParsekLog.Warn("KSCSpawn",
                            $"Spawn #{recIdx} \"{rec.VesselName}\": route-through spawn requested " +
                            "but body resolution failed — falling back to validated snapshot respawn");
                    }
                }

                if (spawnedPid == 0)
                {
                    ConfigNode validatedSpawnSnapshot = VesselSpawner.BuildValidatedRespawnSnapshot(
                        spawnSnapshot,
                        rec,
                        spawnUT,
                        $"KSC spawn #{recIdx} ({rec.VesselName})");
                    if (validatedSpawnSnapshot == null)
                    {
                        ParsekLog.Warn("KSCSpawn",
                            $"Spawn FAILED for #{recIdx} \"{rec.VesselName}\" — spawn snapshot validation failed");
                        return;
                    }

                    spawnSnapshot = validatedSpawnSnapshot;
                    spawnedPid = VesselSpawner.RespawnVessel(validatedSpawnSnapshot, excludeCrew);
                }

                if (spawnedPid != 0)
                {
                    rec.VesselSpawned = true;
                    rec.SpawnedVesselPersistentId = spawnedPid;

                    // Log spawn position for post-spawn diagnosis (#BugB)
                    string latStr = spawnSnapshot.GetValue("lat") ?? "?";
                    string lonStr = spawnSnapshot.GetValue("lon") ?? "?";
                    string altStr = spawnSnapshot.GetValue("alt") ?? "?";
                    string sitStr = spawnSnapshot.GetValue("sit") ?? "?";
                    ParsekLog.Info("KSCSpawn",
                        $"Vessel spawned for #{recIdx} \"{rec.VesselName}\" " +
                        $"pid={spawnedPid} sit={sitStr} lat={latStr} lon={lonStr} alt={altStr}" +
                        (isEva ? $" eva={rec.EvaCrewName}" : "") +
                        " — will appear in Tracking Station");
                }
                else
                {
                    ParsekLog.Warn("KSCSpawn",
                        $"Spawn FAILED for #{recIdx} \"{rec.VesselName}\" — spawn path returned 0");
                }
            }
            catch (Exception ex)
            {
                ParsekLog.Error("KSCSpawn",
                    $"Spawn exception for #{recIdx} \"{rec.VesselName}\": {ex}");
            }
        }

        /// <summary>
        /// Clean up a KSC ghost — stop FX, destroy canopies and GameObject.
        /// </summary>
        void DestroyKscGhost(GhostPlaybackState state, int index)
        {
            if (state == null) return;

            // Detach active particle systems so smoke trails linger (#107)
            if (state.engineInfos != null)
                foreach (var info in state.engineInfos.Values)
                    GhostPlaybackLogic.DetachAndLingerParticleSystems(info.particleSystems, info.kspEmitters);
            if (state.rcsInfos != null)
                foreach (var info in state.rcsInfos.Values)
                    GhostPlaybackLogic.DetachAndLingerParticleSystems(info.particleSystems, info.kspEmitters);

            GhostPlaybackLogic.DestroyAllFakeCanopies(state);
            if (state.ghost != null)
                Destroy(state.ghost);

            ParsekLog.VerboseRateLimited("KSCGhost", "ghost-destroyed",
                $"Ghost #{index} destroyed", 2.0);
        }

        #endregion

        void OnDestroy()
        {
            GameEvents.onGamePause.Remove(OnGamePause);
            GameEvents.onGameUnpause.Remove(OnGameUnpause);

            // Clean up all KSC ghosts (primary + overlap)
            int primaryCount = kscGhosts.Count;
            foreach (var kv in kscGhosts)
                DestroyKscGhost(kv.Value, kv.Key);
            kscGhosts.Clear();

            int overlapCount = 0;
            foreach (var kv in kscOverlapGhosts)
            {
                overlapCount += kv.Value.Count;
                for (int i = 0; i < kv.Value.Count; i++)
                    DestroyKscGhost(kv.Value[i], kv.Key);
            }
            kscOverlapGhosts.Clear();
            if (primaryCount + overlapCount > 0)
                ParsekLog.Info("KSCGhost", $"Destroyed {primaryCount} primary + {overlapCount} overlap KSC ghosts");
            loggedGhostSpawn.Clear();
            loggedReshow.Clear();
            kscSpawnAttempted.Clear();
            loggedPlaybackDisabledPastEndSpawnAttempts.Clear();

            ParsekLog.Info("KSC", "ParsekKSC destroyed");
            ui?.Cleanup();
            if (toolbarControl != null)
            {
                toolbarControl.OnDestroy();
                Destroy(toolbarControl);
            }
        }
    }
}
