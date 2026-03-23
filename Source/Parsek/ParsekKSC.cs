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

        private const double DefaultLoopIntervalSeconds = 10.0;
        private const double MinLoopDurationSeconds = 1.0;
        private const double MinCycleDuration = 1.0;
        // Safety cap for overlap ghosts. Natural phase expiration keeps count bounded for
        // well-behaved recordings, but pathological cases (very short duration, very negative
        // interval) could spawn many before expiration catches up.
        private const int MaxOverlapGhostsPerRecording = 20;

        // Distance culling: skip part events and deactivate ghosts beyond this range from camera.
        // 25km matches Kerbal Konstructs' default activation range for statics.
        private const float GhostCullDistanceSq = 25000f * 25000f;

        void Start()
        {
            ParsekLog.Info("KSC", "ParsekKSC starting in Space Center scene");

            ui = new ParsekUI(ParsekUI.UIMode.KSC);

            toolbarControl = gameObject.AddComponent<ToolbarControl>();
            toolbarControl.AddToAllToolbars(
                () => { showUI = true; ParsekLog.Verbose("KSC", "Toolbar button ON"); },
                () => { showUI = false; ParsekLog.Verbose("KSC", "Toolbar button OFF"); },
                ApplicationLauncher.AppScenes.SPACECENTER,
                ParsekFlight.MODID, "parsekKSCButton",
                "Parsek/Textures/parsek_38",
                "Parsek/Textures/parsek_24",
                ParsekFlight.MODNAME
            );

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

            windowRect = ClickThruBlocker.GUILayoutWindow(
                GetInstanceID(), windowRect, ui.DrawWindow,
                "Parsek", ui.GetOpaqueWindowStyle(), GUILayout.Width(250));

            ui.DrawRecordingsWindowIfOpen(windowRect);
            ui.DrawActionsWindowIfOpen(windowRect);
            ui.DrawSettingsWindowIfOpen(windowRect);
        }

        #region Ghost Playback

        void Update()
        {
            var committed = RecordingStore.CommittedRecordings;
            if (committed.Count == 0) return;

            double currentUT = Planetarium.GetUniversalTime();

            float warpRate = TimeWarp.CurrentRate;
            bool suppressGhosts = GhostPlaybackLogic.ShouldSuppressGhosts(warpRate);
            bool suppressVisualFx = GhostPlaybackLogic.ShouldSuppressVisualFx(warpRate);

            if (suppressGhosts)
            {
                // High time warp: hide primary ghosts, destroy overlap ghosts.
                // KSC scene does not apply resource deltas (flight-only), so no
                // ApplyResourceDeltas call needed here.
                loggedReshow.Clear();
                foreach (var kvp in kscGhosts)
                    if (kvp.Value.ghost != null && kvp.Value.ghost.activeSelf)
                    {
                        kvp.Value.ghost.SetActive(false);
                        ParsekLog.Info("KSCGhost",
                            $"Ghost #{kvp.Key} hidden: warp {warpRate.ToString("F1", CultureInfo.InvariantCulture)}x > {GhostPlaybackLogic.GhostHideWarpThreshold}x");
                    }
                foreach (int key in new List<int>(kscOverlapGhosts.Keys))
                    DestroyAllKscOverlapGhosts(key);
                return;
            }

            for (int i = 0; i < committed.Count; i++)
            {
                var rec = committed[i];
                if (!ShouldShowInKSC(rec))
                {
                    // Recording no longer eligible (disabled, wrong body, etc.)
                    // — clean up any active ghosts so they don't linger in the scene.
                    if (kscGhosts.ContainsKey(i))
                    {
                        ParsekLog.Info("KSCGhost",
                            $"Ghost #{i} \"{rec.VesselName}\" no longer eligible — destroying");
                        DestroyKscGhost(kscGhosts[i], i);
                        kscGhosts.Remove(i);
                        loggedGhostSpawn.Remove(i);
                    }
                    DestroyAllKscOverlapGhosts(i);
                    continue;
                }

                // Branch: looping recordings check interval sign for overlap support
                if (rec.LoopPlayback)
                {
                    double duration = rec.EndUT - rec.StartUT;
                    if (duration <= MinLoopDurationSeconds) continue;

                    double intervalSeconds = GetLoopIntervalSeconds(rec);
                    if (intervalSeconds < 0)
                    {
                        // Negative interval: multi-ghost overlap path
                        UpdateOverlapKsc(i, rec, currentUT, intervalSeconds, duration, suppressVisualFx);
                        continue;
                    }

                    // Positive/zero interval: single-ghost path — clean up any leftover overlaps
                    DestroyAllKscOverlapGhosts(i);

                    double targetUT;
                    int cycleIndex;
                    bool inPauseWindow;
                    bool inRange = TryComputeLoopUT(rec, currentUT,
                        out targetUT, out cycleIndex, out inPauseWindow);

                    UpdateSingleGhostKsc(i, rec, currentUT, targetUT, cycleIndex,
                        inRange, inPauseWindow, suppressVisualFx);
                }
                else
                {
                    // Non-looping: raw UT range check
                    DestroyAllKscOverlapGhosts(i);
                    bool inRange = currentUT >= rec.StartUT && currentUT <= rec.EndUT;
                    UpdateSingleGhostKsc(i, rec, currentUT, currentUT, 0, inRange, false, suppressVisualFx);
                }
            }
        }

        /// <summary>
        /// Single-ghost playback path (positive/zero loop interval, or non-looping).
        /// </summary>
        void UpdateSingleGhostKsc(int recIdx, Recording rec,
            double currentUT, double targetUT, int cycleIndex,
            bool inRange, bool inPauseWindow, bool suppressVisualFx)
        {
            GhostPlaybackState state;
            kscGhosts.TryGetValue(recIdx, out state);
            bool ghostActive = state != null && state.ghost != null;

            if (inRange && !inPauseWindow)
            {
                // Loop cycle change: destroy + respawn to guarantee clean visual state
                if (ghostActive && rec.LoopPlayback && state.loopCycleIndex != cycleIndex)
                {
                    int oldCycle = state.loopCycleIndex;
                    TriggerExplosionIfDestroyed(state, rec, recIdx);
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
                    if (loggedGhostSpawn.Add(recIdx))
                        ParsekLog.Info("KSCGhost",
                            $"Ghost #{recIdx} \"{rec.VesselName}\" entered range: " +
                            $"targetUT={targetUT:F1} recUT=[{rec.StartUT:F1},{rec.EndUT:F1}] " +
                            $"cycle={cycleIndex} loop={rec.LoopPlayback} " +
                            $"terminal={rec.TerminalStateValue}");
                }
                else if (!state.ghost.activeSelf)
                {
                    state.ghost.SetActive(true);
                    if (loggedReshow.Add(recIdx))
                        ParsekLog.Info("KSCGhost",
                            $"Ghost #{recIdx} \"{rec.VesselName}\" re-shown after warp-down");
                }

                InterpolateAndPositionKsc(
                    state.ghost, rec.Points,
                    ref state.playbackIndex, targetUT,
                    rec.RecordingFormatVersion >= 5);

                // Distance culling: skip expensive part events for ghosts too far from camera
                if (IsGhostInCullRange(state.ghost))
                {
                    GhostPlaybackLogic.ApplyPartEvents(recIdx, rec, targetUT, state);
                    GhostPlaybackLogic.ApplyFlagEvents(state, rec, targetUT);
                }
                if (suppressVisualFx)
                    GhostPlaybackLogic.StopAllRcsEmissions(state);
                else
                    GhostPlaybackLogic.RestoreAllRcsEmissions(state);

                if (!state.explosionFired && targetUT >= rec.EndUT)
                    TriggerExplosionIfDestroyed(state, rec, recIdx);
            }
            else if (ghostActive)
            {
                if (inPauseWindow)
                {
                    if (!state.explosionFired)
                        TriggerExplosionIfDestroyed(state, rec, recIdx);
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
                    ParsekLog.Verbose("KSCGhost",
                        $"Ghost #{recIdx} \"{rec.VesselName}\" exited range at UT {currentUT:F1}");
                    DestroyKscGhost(state, recIdx);
                    kscGhosts.Remove(recIdx);
                    loggedGhostSpawn.Remove(recIdx);
                }
            }
        }

        /// <summary>
        /// Multi-ghost overlap path for negative loop intervals.
        /// Multiple ghosts from different cycles visible simultaneously.
        /// Simplified version of ParsekFlight.UpdateOverlapLoopPlayback
        /// (no camera logic, no reentry FX).
        /// </summary>
        void UpdateOverlapKsc(int recIdx, Recording rec,
            double currentUT, double intervalSeconds, double duration, bool suppressVisualFx)
        {
            GhostPlaybackState primaryState;
            kscGhosts.TryGetValue(recIdx, out primaryState);
            bool primaryActive = primaryState != null && primaryState.ghost != null;

            if (currentUT < rec.StartUT)
            {
                if (primaryActive) { DestroyKscGhost(primaryState, recIdx); kscGhosts.Remove(recIdx); }
                DestroyAllKscOverlapGhosts(recIdx);
                return;
            }

            double cycleDuration = duration + intervalSeconds;
            if (cycleDuration < MinCycleDuration) cycleDuration = MinCycleDuration;

            int firstCycle, lastCycle;
            GhostPlaybackLogic.GetActiveCycles(currentUT, rec.StartUT, rec.EndUT,
                intervalSeconds, MaxOverlapGhostsPerRecording, out firstCycle, out lastCycle);

            // Ensure overlap list exists
            List<GhostPlaybackState> overlaps;
            if (!kscOverlapGhosts.TryGetValue(recIdx, out overlaps))
            {
                overlaps = new List<GhostPlaybackState>();
                kscOverlapGhosts[recIdx] = overlaps;
            }

            bool srfRel = rec.RecordingFormatVersion >= 5;

            // --- Primary ghost = newest cycle (lastCycle) ---
            // primaryActive already guarantees primaryState != null && ghost != null
            bool primaryCycleChanged = !primaryActive
                || primaryState.loopCycleIndex != lastCycle;

            if (primaryCycleChanged)
            {
                // Move old primary to overlap list (don't destroy — it keeps playing)
                if (primaryActive)
                {
                    kscGhosts.Remove(recIdx);
                    overlaps.Add(primaryState);
                    ParsekLog.Verbose("KSCGhost",
                        $"Ghost #{recIdx} cycle={primaryState.loopCycleIndex} moved to overlap list");
                }

                // Spawn new primary for lastCycle
                primaryState = SpawnKscGhost(rec, recIdx);
                if (primaryState == null) return;
                primaryState.loopCycleIndex = lastCycle;
                kscGhosts[recIdx] = primaryState;
                ParsekLog.Info("KSCGhost",
                    $"Ghost #{recIdx} \"{rec.VesselName}\" overlap spawn cycle={lastCycle}");
            }

            // Position and animate primary (SpawnKscGhost above guarantees non-null)
            {
                double cycleStartUT = rec.StartUT + lastCycle * cycleDuration;
                double phase = currentUT - cycleStartUT;
                if (phase < 0) phase = 0;
                if (phase > duration) phase = duration;
                double loopUT = rec.StartUT + phase;

                InterpolateAndPositionKsc(primaryState.ghost, rec.Points,
                    ref primaryState.playbackIndex, loopUT, srfRel);

                if (IsGhostInCullRange(primaryState.ghost))
                {
                    GhostPlaybackLogic.ApplyPartEvents(recIdx, rec, loopUT, primaryState);
                    GhostPlaybackLogic.ApplyFlagEvents(primaryState, rec, loopUT);
                }
                if (suppressVisualFx)
                    GhostPlaybackLogic.StopAllRcsEmissions(primaryState);
                else
                    GhostPlaybackLogic.RestoreAllRcsEmissions(primaryState);

                if (!primaryState.explosionFired && phase >= duration)
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

                int cycle = ovState.loopCycleIndex;
                double cycleStart = rec.StartUT + cycle * cycleDuration;
                double phase = currentUT - cycleStart;

                if (phase > duration)
                {
                    // Cycle expired — position at end, explode, destroy
                    if (rec.Points.Count > 0)
                        PositionGhostAtPoint(ovState.ghost,
                            rec.Points[rec.Points.Count - 1], srfRel);
                    TriggerExplosionIfDestroyed(ovState, rec, recIdx);
                    ParsekLog.Verbose("KSCGhost",
                        $"Ghost #{recIdx} overlap cycle={cycle} expired, destroying");
                    DestroyKscGhost(ovState, recIdx);
                    overlaps.RemoveAt(j);
                    continue;
                }

                if (phase < 0) phase = 0;
                double loopUT = rec.StartUT + phase;

                InterpolateAndPositionKsc(ovState.ghost, rec.Points,
                    ref ovState.playbackIndex, loopUT, srfRel);

                if (IsGhostInCullRange(ovState.ghost))
                {
                    GhostPlaybackLogic.ApplyPartEvents(recIdx, rec, loopUT, ovState);
                    GhostPlaybackLogic.ApplyFlagEvents(ovState, rec, loopUT);
                }
                if (suppressVisualFx)
                    GhostPlaybackLogic.StopAllRcsEmissions(ovState);
                else
                    GhostPlaybackLogic.RestoreAllRcsEmissions(ovState);
            }
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
        /// Filter recordings for KSC ghost display.
        /// </summary>
        internal static bool ShouldShowInKSC(Recording rec)
        {
            if (!rec.PlaybackEnabled) return false;
            if (rec.Points == null || rec.Points.Count < 2) return false;
            // Only Kerbin recordings (KSC is on Kerbin)
            if (rec.Points[0].bodyName != "Kerbin") return false;
            return true;
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
                partTree = GhostVisualBuilder.BuildPartSubtreeMap(snapshot)
            };

            PopulateGhostInfoDictionaries(state, buildResult);

            GhostPlaybackLogic.InitializeInventoryPlacementVisibility(rec, state);

            // Initialize flag event index — flags are spawned as real vessels on-demand by ApplyFlagEvents
            GhostPlaybackLogic.InitializeFlagVisibility(rec, state);

            ParsekLog.Info("KSCGhost",
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
        /// Populate the GhostPlaybackState info dictionaries from a GhostBuildResult.
        /// Transfers parachute, jettison, engine, deployable, heat, light, fairing,
        /// RCS, robotic, and color changer infos into the state's lookup dictionaries.
        /// </summary>
        private static void PopulateGhostInfoDictionaries(
            GhostPlaybackState state, GhostBuildResult buildResult)
        {
            if (buildResult.parachuteInfos != null)
            {
                state.parachuteInfos = new Dictionary<uint, ParachuteGhostInfo>();
                for (int i = 0; i < buildResult.parachuteInfos.Count; i++)
                    state.parachuteInfos[buildResult.parachuteInfos[i].partPersistentId] = buildResult.parachuteInfos[i];
            }

            if (buildResult.jettisonInfos != null)
            {
                state.jettisonInfos = new Dictionary<uint, JettisonGhostInfo>();
                for (int i = 0; i < buildResult.jettisonInfos.Count; i++)
                    state.jettisonInfos[buildResult.jettisonInfos[i].partPersistentId] = buildResult.jettisonInfos[i];
            }

            if (buildResult.engineInfos != null)
            {
                state.engineInfos = new Dictionary<ulong, EngineGhostInfo>();
                for (int i = 0; i < buildResult.engineInfos.Count; i++)
                {
                    ulong key = FlightRecorder.EncodeEngineKey(
                        buildResult.engineInfos[i].partPersistentId, buildResult.engineInfos[i].moduleIndex);
                    state.engineInfos[key] = buildResult.engineInfos[i];
                }
            }

            if (buildResult.deployableInfos != null)
            {
                state.deployableInfos = new Dictionary<uint, DeployableGhostInfo>();
                for (int i = 0; i < buildResult.deployableInfos.Count; i++)
                    state.deployableInfos[buildResult.deployableInfos[i].partPersistentId] = buildResult.deployableInfos[i];
            }

            if (buildResult.heatInfos != null)
            {
                state.heatInfos = new Dictionary<uint, HeatGhostInfo>();
                for (int i = 0; i < buildResult.heatInfos.Count; i++)
                    state.heatInfos[buildResult.heatInfos[i].partPersistentId] = buildResult.heatInfos[i];
            }

            if (buildResult.lightInfos != null)
            {
                state.lightInfos = new Dictionary<uint, LightGhostInfo>();
                state.lightPlaybackStates =
                    new Dictionary<uint, LightPlaybackState>();
                for (int i = 0; i < buildResult.lightInfos.Count; i++)
                {
                    state.lightInfos[buildResult.lightInfos[i].partPersistentId] = buildResult.lightInfos[i];
                    state.lightPlaybackStates[buildResult.lightInfos[i].partPersistentId] =
                        new LightPlaybackState();
                }
            }

            if (buildResult.fairingInfos != null)
            {
                state.fairingInfos = new Dictionary<uint, FairingGhostInfo>();
                for (int i = 0; i < buildResult.fairingInfos.Count; i++)
                    state.fairingInfos[buildResult.fairingInfos[i].partPersistentId] = buildResult.fairingInfos[i];
            }

            if (buildResult.rcsInfos != null)
            {
                state.rcsInfos = new Dictionary<ulong, RcsGhostInfo>();
                for (int i = 0; i < buildResult.rcsInfos.Count; i++)
                {
                    ulong key = FlightRecorder.EncodeEngineKey(
                        buildResult.rcsInfos[i].partPersistentId, buildResult.rcsInfos[i].moduleIndex);
                    state.rcsInfos[key] = buildResult.rcsInfos[i];
                }
            }

            if (buildResult.roboticInfos != null)
            {
                state.roboticInfos = new Dictionary<ulong, RoboticGhostInfo>();
                for (int i = 0; i < buildResult.roboticInfos.Count; i++)
                {
                    ulong key = FlightRecorder.EncodeEngineKey(
                        buildResult.roboticInfos[i].partPersistentId, buildResult.roboticInfos[i].moduleIndex);
                    state.roboticInfos[key] = buildResult.roboticInfos[i];
                }
            }

            if (buildResult.colorChangerInfos != null)
                state.colorChangerInfos = GhostVisualBuilder.GroupColorChangersByPartId(buildResult.colorChangerInfos);
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
            ref int cachedIndex, double targetUT,
            bool surfaceRelativeRotation)
        {
            if (points == null || points.Count == 0)
            {
                ghost.SetActive(false);
                return;
            }

            int indexBefore = TrajectoryMath.FindWaypointIndex(points, ref cachedIndex, targetUT);

            if (indexBefore < 0)
            {
                // Before recording start — position at first point
                PositionGhostAtPoint(ghost, points[0], surfaceRelativeRotation);
                return;
            }

            TrajectoryPoint before = points[indexBefore];
            TrajectoryPoint after = points[indexBefore + 1];

            // Stop positioning when trajectory leaves Kerbin
            if (before.bodyName != "Kerbin" || after.bodyName != "Kerbin")
            {
                ghost.SetActive(false);
                return;
            }

            double segmentDuration = after.ut - before.ut;
            if (segmentDuration <= 0.0001)
            {
                PositionGhostAtPoint(ghost, before, surfaceRelativeRotation);
                return;
            }

            float t = (float)((targetUT - before.ut) / segmentDuration);
            t = Mathf.Clamp01(t);

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
            // Uses bodyBefore's transform for rotation (not interpolated between bodies).
            // Negligible for KSC: both points are Kerbin, body rotation is constant.
            ghost.transform.rotation = surfaceRelativeRotation
                ? bodyBefore.bodyTransform.rotation * interpolatedRot
                : interpolatedRot;
        }

        /// <summary>
        /// Position ghost at a single trajectory point (no interpolation).
        /// </summary>
        void PositionGhostAtPoint(GameObject ghost, TrajectoryPoint point,
            bool surfaceRelativeRotation)
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
            ghost.transform.rotation = surfaceRelativeRotation
                ? body.bodyTransform.rotation * sanitized
                : sanitized;

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
        internal static bool TryComputeLoopUT(
            Recording rec,
            double currentUT,
            out double loopUT,
            out int cycleIndex,
            out bool inPauseWindow)
        {
            loopUT = rec != null ? rec.StartUT : 0;
            cycleIndex = 0;
            inPauseWindow = false;
            if (rec == null || rec.Points == null || rec.Points.Count < 2) return false;
            if (currentUT < rec.StartUT) return false;

            double duration = rec.EndUT - rec.StartUT;
            if (duration <= MinLoopDurationSeconds) return false;

            double intervalSeconds = GetLoopIntervalSeconds(rec);
            double cycleDuration = duration + intervalSeconds;
            if (cycleDuration <= MinLoopDurationSeconds)
                cycleDuration = duration;

            double elapsed = currentUT - rec.StartUT;
            cycleIndex = (int)Math.Floor(elapsed / cycleDuration);
            if (cycleIndex < 0) cycleIndex = 0;

            double cycleTime = elapsed - (cycleIndex * cycleDuration);
            if (intervalSeconds > 0 && cycleTime > duration)
            {
                inPauseWindow = true;
                loopUT = rec.EndUT;
                return true;
            }

            loopUT = rec.StartUT + Math.Min(cycleTime, duration);
            return true;
        }

        /// <summary>
        /// Get the loop interval for a recording. Matches ParsekFlight logic:
        /// clamped so cycleDuration is always >= MinCycleDuration.
        /// Negative intervals mean shorter cycles (overlapping launches from KSC).
        /// </summary>
        internal static double GetLoopIntervalSeconds(Recording rec)
        {
            double globalInterval = ParsekSettings.Current?.autoLoopIntervalSeconds
                                    ?? DefaultLoopIntervalSeconds;
            return GhostPlaybackLogic.ResolveLoopInterval(
                rec, globalInterval, DefaultLoopIntervalSeconds, MinCycleDuration);
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
                    $"Explosion suppressed for ghost #{recIdx} \"{rec.VesselName}\": warp > {GhostPlaybackLogic.FxSuppressWarpThreshold}x");
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

            var explosion = GhostVisualBuilder.SpawnExplosionFx(worldPos, vesselLength);
            if (explosion != null)
                Destroy(explosion, 6f);

            GhostPlaybackLogic.HideAllGhostParts(state);
        }

        /// <summary>
        /// Clean up a KSC ghost — stop FX, destroy canopies and GameObject.
        /// </summary>
        void DestroyKscGhost(GhostPlaybackState state, int index)
        {
            if (state == null) return;

            // Stop engine particle systems
            StopParticleSystems(state.engineInfos);

            // Stop RCS particle systems
            StopRcsParticleSystems(state.rcsInfos);

            GhostPlaybackLogic.DestroyAllFakeCanopies(state);
            if (state.ghost != null)
                Destroy(state.ghost);

            ParsekLog.Verbose("KSCGhost", $"Ghost #{index} destroyed");
        }

        /// <summary>
        /// Stop and clear all particle systems in engine ghost infos.
        /// Extracted from DestroyKscGhost to deduplicate engine/RCS cleanup.
        /// </summary>
        private static void StopParticleSystems(Dictionary<ulong, EngineGhostInfo> infos)
        {
            if (infos == null) return;
            foreach (var kv in infos)
            {
                if (kv.Value.particleSystems == null) continue;
                for (int i = 0; i < kv.Value.particleSystems.Count; i++)
                {
                    var ps = kv.Value.particleSystems[i];
                    if (ps != null)
                    {
                        ps.Stop(true, ParticleSystemStopBehavior.StopEmittingAndClear);
                        ps.Clear(true);
                    }
                }
            }
        }

        /// <summary>
        /// Stop and clear all particle systems in RCS ghost infos.
        /// Extracted from DestroyKscGhost to deduplicate engine/RCS cleanup.
        /// </summary>
        private static void StopRcsParticleSystems(Dictionary<ulong, RcsGhostInfo> infos)
        {
            if (infos == null) return;
            foreach (var kv in infos)
            {
                if (kv.Value.particleSystems == null) continue;
                for (int i = 0; i < kv.Value.particleSystems.Count; i++)
                {
                    var ps = kv.Value.particleSystems[i];
                    if (ps != null)
                    {
                        ps.Stop(true, ParticleSystemStopBehavior.StopEmittingAndClear);
                        ps.Clear(true);
                    }
                }
            }
        }

        #endregion

        void OnDestroy()
        {
            // Clean up all KSC ghosts (primary + overlap)
            foreach (var kv in kscGhosts)
                DestroyKscGhost(kv.Value, kv.Key);
            kscGhosts.Clear();

            foreach (var kv in kscOverlapGhosts)
                for (int i = 0; i < kv.Value.Count; i++)
                    DestroyKscGhost(kv.Value[i], kv.Key);
            kscOverlapGhosts.Clear();
            loggedGhostSpawn.Clear();
            loggedReshow.Clear();

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
